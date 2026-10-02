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
    /// 還把這個團隊當主責的專案名稱。有的話非管理者不能刪團隊：主責必填，刪了專案就沒有主責。
    /// 只當協作的可以刪，刪了只會讓看得到的人變少。管理者不受這條限制（0.4.104）。
    /// </summary>
    public static async Task<List<string>> PrimaryProjectTitlesAsync(BackendDBContext context, int teamId)
    {
        return await context.ProjectTeam.AsNoTracking()
            .Where(x => x.TeamId == teamId && x.IsPrimary)
            .Select(x => x.Project!.Title)
            .OrderBy(x => x)
            .ToListAsync();
    }

    /// <summary>刪除被擋時給使用者看的訊息。</summary>
    public static string PrimaryInUseMessage(IEnumerable<string> titles)
        => $"這個團隊還是以下專案的主責團隊，請先到專案改主責團隊再刪除：{string.Join("、", titles)}";

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
