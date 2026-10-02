using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 升級時把舊資料轉成「主責＋協作團隊」（0.4.102），並補舊會議的上傳者。每次啟動都跑，冪等。
///
/// <list type="bullet">
/// <item><b>專案成員 → 團隊</b>（0.4.99～0.4.100 的資料；只在舊的 <c>ProjectMember</c> 表還在時做，做完就刪表，所以只會跑一次）：
/// 有成員的專案建一個同名團隊、把成員放進去、設成主責——升級前後看得到的人完全一樣。
/// 沒有成員的專案主責掛到一個沒有成員的「待分配」團隊，只有管理者看得到。</item>
/// <item><b>補主責</b>（0.4.101 的資料）：還沒有主責的專案，取已掛團隊中 Id 最小的一筆設成主責；
/// 完全沒掛團隊的（0.4.101 的「公開」專案）主責掛「待分配」。0.4.102 起沒有公開專案，
/// 這些專案在管理者重新指定之前只有管理者看得到——寧可暫時看不到，也不要誤開給全公司。</item>
/// <item><b>上傳者</b>：<c>CreatedByUserId</c> 為 null 的會議，取 AI 用量帳本裡這場會議最早一筆轉錄的 <c>UserId</c>。
/// 帳本 0.4.80 才開始記，更早的推不回來，維持 null（歸屬專案之前只有管理者看得到）。</item>
/// </list>
/// <para>
/// ⚠️ <c>ProjectMember</c> 已從 EF 模型移除，但 0.4.101 的 migration 刻意<b>不刪表</b>，留給這裡用原生 SQL 讀；
/// 刪表由這裡在轉換成功後負責。
/// </para>
/// </summary>
public sealed class TeamConversionService
{
    /// <summary>沒有歸屬的舊專案要掛的主責團隊。刻意沒有成員：只有管理者看得到，等管理者分配。</summary>
    public const string UnassignedGroupName = "待分配";

    private readonly BackendDBContext context;
    private readonly ILogger<TeamConversionService> logger;

    public TeamConversionService(BackendDBContext context, ILogger<TeamConversionService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task RunAsync()
    {
        var converted = await ConvertProjectMembersAsync();
        var primaries = await EnsurePrimaryTeamsAsync();
        var creators = await BackfillMeetingCreatorsAsync();
        logger.LogInformation(
            "Team conversion completed. ProjectsConverted={Projects}, PrimaryTeamsAssigned={Primaries}, MeetingCreatorsAssigned={Creators}",
            converted,
            primaries,
            creators);
    }

    /// <summary>每個專案要恰好一筆主責；0.4.101 的資料沒有主責欄位（全部是 false）。</summary>
    private async Task<int> EnsurePrimaryTeamsAsync()
    {
        var projects = await context.Project
            .Include(p => p.Teams)
            .Where(p => !p.Teams.Any(t => t.IsPrimary))
            .ToListAsync();
        if (projects.Count == 0)
        {
            return 0;
        }

        Team? unassigned = null;
        foreach (var project in projects)
        {
            var first = project.Teams.OrderBy(t => t.TeamId).FirstOrDefault();
            if (first is not null)
            {
                first.IsPrimary = true;
                continue;
            }

            unassigned ??= await context.Team.FirstOrDefaultAsync(x => x.Name == UnassignedGroupName)
                ?? CreateTeam(UnassignedGroupName, UnassignedDescription, []);
            project.Teams.Add(new ProjectTeam { Team = unassigned, IsPrimary = true });
        }

        await context.SaveChangesAsync();
        logger.LogInformation("Primary teams assigned to legacy projects. Projects={Projects}", projects.Count);
        return projects.Count;
    }

    private const string UnassignedDescription = "升級時沒有歸屬的舊專案都放在這裡，只有管理者看得到；請改指定正確的主責團隊。";

    private async Task<int> ConvertProjectMembersAsync()
    {
        if (!await LegacyTableExistsAsync())
        {
            return 0;
        }

        var memberships = await ReadLegacyMembershipsAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        // 已經有群組的專案不動：保險起見，萬一轉到一半重來也不會重複建群組。
        var projects = await context.Project
            .Where(p => !p.Teams.Any())
            .Select(p => new { p.Id, p.Title })
            .ToListAsync();

        var usedNames = (await context.Team.Select(x => x.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validUserIds = (await context.MyUser.Select(x => x.Id).ToListAsync()).ToHashSet();

        Team? unassigned = null;
        foreach (var project in projects)
        {
            var members = memberships
                .Where(x => x.ProjectId == project.Id && validUserIds.Contains(x.UserId))
                .Select(x => x.UserId)
                .Distinct()
                .ToList();

            Team team;
            if (members.Count == 0)
            {
                unassigned ??= await context.Team.FirstOrDefaultAsync(x => x.Name == UnassignedGroupName)
                    ?? CreateTeam(UnassignedGroupName, UnassignedDescription, usedNames);
                team = unassigned;
            }
            else
            {
                // 新群組一定還沒有成員，members 也已經去重，直接加。
                team = CreateTeam(UniqueName(project.Title, usedNames), $"由專案「{project.Title}」的成員轉換而來。", usedNames);
                foreach (var userId in members)
                {
                    context.UserTeam.Add(new UserTeam { MyUserId = userId, Team = team });
                }
            }

            context.ProjectTeam.Add(new ProjectTeam { ProjectId = project.Id, Team = team, IsPrimary = true });
        }

        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ProjectMember\"");
        await transaction.CommitAsync();

        logger.LogInformation("Legacy project members converted to data groups. Projects={Projects}", projects.Count);
        return projects.Count;
    }

    private Team CreateTeam(string name, string description, HashSet<string> usedNames)
    {
        usedNames.Add(name);
        var team = new Team { Name = name, Description = description, IsEnabled = true };
        context.Team.Add(team);
        return team;
    }

    /// <summary>跟專案同名；撞到既有團隊名稱時加「（專案）」，再撞加序號。</summary>
    internal static string UniqueName(string title, IReadOnlySet<string> usedNames)
    {
        var baseName = string.IsNullOrWhiteSpace(title) ? "未命名專案" : title.Trim();
        if (!usedNames.Contains(baseName))
        {
            return baseName;
        }

        var candidate = $"{baseName}（專案）";
        for (var i = 2; usedNames.Contains(candidate); i++)
        {
            candidate = $"{baseName}（專案 {i}）";
        }

        return candidate;
    }

    private async Task<bool> LegacyTableExistsAsync()
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProjectMember'";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<List<(int ProjectId, int UserId)>> ReadLegacyMembershipsAsync()
    {
        var result = new List<(int, int)>();
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProjectId, MyUserId FROM ProjectMember";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return result;
    }

    private async Task<int> BackfillMeetingCreatorsAsync()
    {
        var meetingIds = await context.Meeting
            .Where(x => x.CreatedByUserId == null)
            .Select(x => x.Id)
            .ToListAsync();
        if (meetingIds.Count == 0)
        {
            return 0;
        }

        var firstUploaders = (await context.AiUsageLog
                .Where(x => x.Feature == AiUsageFeature.Transcription
                    && x.MeetingId != null
                    && x.UserId != null
                    && meetingIds.Contains(x.MeetingId.Value))
                .Select(x => new { MeetingId = x.MeetingId!.Value, UserId = x.UserId!.Value, x.OccurredAt })
                .ToListAsync())
            .GroupBy(x => x.MeetingId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.OccurredAt).First().UserId);
        if (firstUploaders.Count == 0)
        {
            return 0;
        }

        var meetings = await context.Meeting
            .Where(x => firstUploaders.Keys.Contains(x.Id))
            .ToListAsync();
        foreach (var meeting in meetings)
        {
            meeting.CreatedByUserId = firstUploaders[meeting.Id];
        }

        await context.SaveChangesAsync();
        return meetings.Count;
    }
}
