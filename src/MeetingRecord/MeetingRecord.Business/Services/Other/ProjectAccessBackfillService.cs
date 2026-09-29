using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 把 0.4.98 以前的資料補進專案權限（0.4.99）。每次啟動都跑，冪等——只補還沒有的。
///
/// <list type="bullet">
/// <item><b>負責人</b>：還沒有負責人的專案，拿 <c>Project.Owner</c> 這個手打的姓名去對 <c>MyUser.Name</c>。
/// 要剛好對到<b>一個</b>帳號才算數；對不到或同名好幾個都跳過，那個專案就只有管理者看得到，由管理者補指派。</item>
/// <item><b>上傳者</b>：<c>CreatedByUserId</c> 還是 null 的會議，取 AI 用量帳本裡這場會議<b>最早一筆轉錄</b>的 <c>UserId</c>。
/// 帳本 0.4.80 才開始記，更早的會議推不回來，維持 null（歸屬專案之前只有管理者看得到）。</item>
/// </list>
/// </summary>
public sealed class ProjectAccessBackfillService
{
    private readonly BackendDBContext context;
    private readonly ILogger<ProjectAccessBackfillService> logger;

    public ProjectAccessBackfillService(BackendDBContext context, ILogger<ProjectAccessBackfillService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task RunAsync()
    {
        var owners = await BackfillOwnersAsync();
        var creators = await BackfillMeetingCreatorsAsync();
        logger.LogInformation(
            "Project access backfill completed. OwnersAssigned={Owners}, MeetingCreatorsAssigned={Creators}",
            owners,
            creators);
    }

    private async Task<int> BackfillOwnersAsync()
    {
        var ownedProjectIds = await context.ProjectMember
            .Where(x => x.Role == ProjectMemberRole.Owner)
            .Select(x => x.ProjectId)
            .ToListAsync();

        var projects = await context.Project
            .Where(x => !ownedProjectIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Owner })
            .ToListAsync();
        if (projects.Count == 0)
        {
            return 0;
        }

        // 只認剛好一個同名帳號的：同名兩個以上時猜錯的代價是把專案給錯人看。
        var userIdByName = (await context.MyUser.Select(x => new { x.Id, x.Name }).ToListAsync())
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim())
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id);

        var existing = (await context.ProjectMember.Select(x => new { x.ProjectId, x.MyUserId }).ToListAsync())
            .Select(x => (x.ProjectId, x.MyUserId))
            .ToHashSet();

        var assigned = 0;
        foreach (var project in projects)
        {
            if (!userIdByName.TryGetValue((project.Owner ?? string.Empty).Trim(), out var userId))
            {
                continue;
            }

            if (existing.Contains((project.Id, userId)))
            {
                // 已經是協作者：升成負責人，不能再插一列（唯一索引）。
                var member = await context.ProjectMember.FirstAsync(x => x.ProjectId == project.Id && x.MyUserId == userId);
                member.Role = ProjectMemberRole.Owner;
            }
            else
            {
                context.ProjectMember.Add(new ProjectMember
                {
                    ProjectId = project.Id,
                    MyUserId = userId,
                    Role = ProjectMemberRole.Owner,
                });
            }

            assigned++;
        }

        await context.SaveChangesAsync();
        return assigned;
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
