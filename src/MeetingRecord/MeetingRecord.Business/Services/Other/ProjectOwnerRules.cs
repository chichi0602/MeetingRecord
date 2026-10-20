using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;

namespace MeetingRecord.Business.Services.Other;

/// <summary>負責人下拉的一個選項（1.0.52）。<see cref="Label"/> 是「姓名（帳號）」，搜尋時姓名、帳號都打得到。</summary>
public sealed record ProjectOwnerCandidate(int Id, string Name, string Account)
{
    public string Label => $"{Name}（{Account}）";
}

/// <summary>
/// 專案負責人的規則（1.0.52），畫面（<c>ProjectService</c>）與 API（<c>ProjectRepository</c>）共用。
/// <list type="bullet">
/// <item>候選人：主責＋協作團隊裡啟用中的帳號——負責人一定看得到這個專案。</item>
/// <item>誰能換負責人：主責團隊成員、目前的負責人、管理者（不然協作團隊的人可以把自己設成負責人，拿到刪除權）。</item>
/// <item>負責人（與管理者）可以刪除專案，以及底下任何人的會議、待辦、附件（判斷在 <see cref="ProjectAccess.IsProjectOwner"/>）。</item>
/// </list>
/// </summary>
public static class ProjectOwnerRules
{
    public const string RequiredMessage = "請選擇負責人。";
    public const string InvalidCandidateMessage = "負責人必須是專案團隊裡啟用中的帳號，請重新選擇。";
    public const string ChangeDeniedMessage = "只有主責團隊的成員或目前的負責人可以變更負責人。";
    public const string DeleteProjectMessage = "只有專案負責人可以刪除專案。";
    public const string DeleteTodoMessage = "只有專案負責人可以刪除待辦。";
    public const string DeleteFileMessage = "只有專案負責人可以刪除附件。";

    /// <summary>這些團隊裡啟用中的帳號，依姓名排序（同一人在多個團隊只列一次）。</summary>
    public static async Task<IReadOnlyList<ProjectOwnerCandidate>> GetCandidatesAsync(
        BackendDBContext context, IEnumerable<int> teamIds, CancellationToken cancellationToken = default)
    {
        var ids = teamIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await context.UserTeam.AsNoTracking()
            .Where(x => ids.Contains(x.TeamId) && x.MyUser!.Status)
            .Select(x => new { x.MyUser!.Id, x.MyUser.Name, x.MyUser.Account })
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. users
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProjectOwnerCandidate(x.Id, x.Name, x.Account))];
    }

    /// <summary>
    /// 驗證選的負責人並回傳要寫入的帳號 Id 與姓名快照；不合法時 <c>Error</c> 不為 null。
    /// </summary>
    public static async Task<(int Id, string Name, string? Error)> ResolveAsync(
        BackendDBContext context, int? ownerUserId, ProjectTeamResolution teams, CancellationToken cancellationToken = default)
    {
        if (ownerUserId is not { } id)
        {
            return (0, string.Empty, RequiredMessage);
        }

        var candidates = await GetCandidatesAsync(context, teams.CollaboratorTeamIds.Append(teams.PrimaryTeamId), cancellationToken);
        return candidates.FirstOrDefault(x => x.Id == id) is { } owner
            ? (owner.Id, owner.Name, null)
            : (0, string.Empty, InvalidCandidateMessage);
    }

    /// <summary>
    /// API 相容（1.0.52 前只有 <c>owner</c> 文字）：用名字在候選人裡找，姓名或帳號剛好對到一位才採用。
    /// </summary>
    public static async Task<ProjectOwnerCandidate?> MatchByNameAsync(
        BackendDBContext context, string? owner, ProjectTeamResolution teams, CancellationToken cancellationToken = default)
    {
        var name = owner?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var candidates = await GetCandidatesAsync(context, teams.CollaboratorTeamIds.Append(teams.PrimaryTeamId), cancellationToken);
        var matches = candidates
            .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Account, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>能不能換這個專案的負責人：主責團隊成員、目前的負責人、管理者。</summary>
    public static bool CanChangeOwner(ProjectAccess access, int projectId)
        => access.CanManageProject(projectId) || access.IsProjectOwner(projectId);
}
