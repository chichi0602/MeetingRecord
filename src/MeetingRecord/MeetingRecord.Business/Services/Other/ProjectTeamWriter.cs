using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 把 <see cref="ProjectAccess.ResolveProjectTeams"/> 的結果寫進專案的 <see cref="ProjectTeam"/>（0.4.102）。
/// 畫面（<c>ProjectService</c>）與 API（<c>ProjectRepository</c>）共用，兩邊規則才不會分岔。
/// </summary>
public static class ProjectTeamWriter
{
    /// <summary>主責團隊要真的存在；協作只留下存在的（畫面或 API 可能帶進已刪除的團隊）。</summary>
    public static async Task<ProjectTeamResolution> ValidateAsync(BackendDBContext context, ProjectTeamResolution resolution)
    {
        if (resolution.Error is not null)
        {
            return resolution;
        }

        var ids = resolution.CollaboratorTeamIds.Append(resolution.PrimaryTeamId).ToList();
        var existing = (await context.Team.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync()).ToHashSet();
        if (!existing.Contains(resolution.PrimaryTeamId))
        {
            return ProjectTeamResolution.Fail("找不到選擇的主責團隊。");
        }

        return resolution with { CollaboratorTeamIds = [.. resolution.CollaboratorTeamIds.Where(existing.Contains)] };
    }

    /// <summary>
    /// 刪除團隊前的檢查：團隊還是某些專案的主責時，非管理者不能刪（主責必填，刪了專案就沒有主責）。
    /// 只當協作的可以刪，刪了只會讓看得到的人變少。管理者不受這條限制（0.4.104）。
    /// 回傳 null 表示可以刪，否則是給使用者看的訊息。
    /// 訊息只列出使用者看得到的專案名稱（0.4.114），看不到的只給數量，否則會洩漏別的部門的專案名稱。
    /// </summary>
    public static async Task<string?> PrimaryInUseMessageAsync(BackendDBContext context, int teamId, ProjectAccess access)
    {
        if (access.IsAdmin)
        {
            return null;
        }

        var projects = await context.ProjectTeam.AsNoTracking()
            .Where(x => x.TeamId == teamId && x.IsPrimary)
            .Select(x => new { x.ProjectId, Title = x.Project!.Title })
            .OrderBy(x => x.Title)
            .ToListAsync();
        if (projects.Count == 0)
        {
            return null;
        }

        var visible = projects.Where(x => access.CanViewProject(x.ProjectId)).Select(x => x.Title).ToList();
        var hidden = projects.Count - visible.Count;
        var list = string.Join("、", visible);
        var hiddenText = hidden > 0 ? $"{(visible.Count > 0 ? "，" : string.Empty)}另有 {hidden} 個你看不到的專案" : string.Empty;
        return $"這個團隊還是以下專案的主責團隊，請先到專案改主責團隊再刪除：{list}{hiddenText}";
    }

    /// <summary>把專案的團隊同步成「恰好一筆主責＋指定的協作」。<paramref name="project"/> 的 Teams 必須已載入。</summary>
    public static void Apply(Project project, ProjectTeamResolution teams)
    {
        var wanted = teams.CollaboratorTeamIds.ToDictionary(id => id, _ => false);
        wanted[teams.PrimaryTeamId] = true;

        foreach (var removed in project.Teams.Where(x => !wanted.ContainsKey(x.TeamId)).ToList())
        {
            project.Teams.Remove(removed);
        }

        foreach (var (teamId, isPrimary) in wanted)
        {
            var link = project.Teams.FirstOrDefault(x => x.TeamId == teamId);
            if (link is null)
            {
                project.Teams.Add(new ProjectTeam { TeamId = teamId, IsPrimary = isPrimary });
            }
            else
            {
                link.IsPrimary = isPrimary;
            }
        }
    }
}
