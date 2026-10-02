using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 目前使用者看得到的範圍（0.4.102）。**只管「誰的資料看得到」**，「能做什麼」是角色的事（<c>IPermissionChecker</c>），
/// 分類只描述「什麼資料」、不影響這裡。管理者 <see cref="IsAdmin"/> 為 true 時不受任何限制，其餘清單可忽略。
/// </summary>
/// <param name="IsAdmin">管理者（<c>MyUser.IsAdmin</c>）。</param>
/// <param name="UserId">使用者 Id；解析不到時為 0，這時什麼專案都看不到。</param>
/// <param name="TeamIds">使用者所屬的團隊（<c>UserTeam</c>）。角色不帶任何團隊。</param>
/// <param name="ProjectIds">看得到的專案：主責或協作團隊與 <see cref="TeamIds"/> 有交集。沒有公開專案。管理者不用這份清單。</param>
public sealed record ProjectAccess(
    bool IsAdmin,
    int UserId,
    IReadOnlyList<int> TeamIds,
    IReadOnlyList<int> ProjectIds)
{
    public bool CanViewProject(int projectId) => IsAdmin || ProjectIds.Contains(projectId);

    /// <summary>
    /// 會議看得到的條件：已歸屬看專案；**未歸屬 → 只有上傳者**。
    /// 上傳者為 null（0.4.80 以前、回填推不回來的舊會議）時只有管理者看得到。
    /// </summary>
    public bool CanViewMeeting(int? projectId, int? createdByUserId)
        => IsAdmin
            || (projectId is { } id
                ? ProjectIds.Contains(id)
                : UserId != 0 && createdByUserId == UserId);

    /// <summary>
    /// 驗證存專案時的團隊選擇，回傳實際要寫入的主責與協作團隊；不合法時回傳錯誤訊息。
    /// <list type="bullet">
    /// <item>主責必填。</item>
    /// <item>非管理者的主責只能選<b>自己所屬</b>的團隊；但主責<b>沒變</b>時照原樣保留——
    /// 協作團隊的人也能改專案內容，不能因為主責不是他的團隊就存不了檔。</item>
    /// <item>協作團隊任何人都可以選任何團隊（拉別的部門進來協作）；主責不重複列在協作裡。</item>
    /// </list>
    /// </summary>
    public ProjectTeamResolution ResolveProjectTeams(int? existingPrimaryTeamId, int? selectedPrimaryTeamId, IEnumerable<int> selectedCollaboratorTeamIds)
    {
        if (selectedPrimaryTeamId is not { } primary)
        {
            return ProjectTeamResolution.Fail("請選擇主責團隊。");
        }

        if (!IsAdmin && primary != existingPrimaryTeamId && !TeamIds.Contains(primary))
        {
            return ProjectTeamResolution.Fail("主責團隊只能選擇自己所屬的團隊。");
        }

        var collaborators = selectedCollaboratorTeamIds.Where(id => id != primary).Distinct().ToList();
        return new ProjectTeamResolution(primary, collaborators, null);
    }

    public IQueryable<Project> Filter(IQueryable<Project> query)
    {
        if (IsAdmin)
        {
            return query;
        }

        var ids = ProjectIds.ToList();
        return query.Where(x => ids.Contains(x.Id));
    }

    /// <summary>與 <see cref="CanViewMeeting"/> 同一條規則的 SQL 版本，兩邊要一起改。</summary>
    public IQueryable<Meeting> Filter(IQueryable<Meeting> query)
    {
        if (IsAdmin)
        {
            return query;
        }

        var ids = ProjectIds.ToList();
        var userId = UserId;
        return query.Where(x =>
            (x.ProjectId != null && ids.Contains(x.ProjectId.Value))
            || (x.ProjectId == null && userId != 0 && x.CreatedByUserId == userId));
    }

    public IQueryable<Todo> Filter(IQueryable<Todo> query)
    {
        if (IsAdmin)
        {
            return query;
        }

        var ids = ProjectIds.ToList();
        return query.Where(x => ids.Contains(x.ProjectId));
    }
}

/// <summary><see cref="ProjectAccess.ResolveProjectTeams"/> 的結果；<see cref="Error"/> 不為 null 時不可存檔。</summary>
public sealed record ProjectTeamResolution(int PrimaryTeamId, IReadOnlyList<int> CollaboratorTeamIds, string? Error)
{
    public static ProjectTeamResolution Fail(string error) => new(0, [], error);
}

/// <summary>
/// 資料可見範圍的**唯一入口**（0.4.102 起以專案的主責＋協作團隊為準）。專案、會議、待辦、附件、AI 問答的每一條讀寫路徑
/// 都要經過這裡取得 <see cref="ProjectAccess"/>，不要在各服務自己查 <c>ProjectTeam</c>／<c>UserTeam</c>。
///
/// <para>
/// 刻意不快取：Blazor 的 scope 跟著整條連線活很久，團隊成員在這段期間可能被管理者改掉；
/// 一次只撈 Id，代價很小。
/// </para>
/// </summary>
public sealed class ProjectAccessService
{
    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider scopeProvider;

    public ProjectAccessService(BackendDBContext context, IRecordAccessScopeProvider scopeProvider)
    {
        this.context = context;
        this.scopeProvider = scopeProvider;
    }

    public async Task<ProjectAccess> GetAsync()
        => await LoadAsync(context, await scopeProvider.GetAsync());

    /// <summary>
    /// 用指定的 DbContext 載入存取範圍。給背景事件觸發的元件（右下角進度面板）用：
    /// 它和頁面共用同一條 Blazor 連線，拿頁面的 DbContext 查會撞上「同一個 context 並行兩個操作」，
    /// 所以要自己開一個短命的 scope 查。
    /// </summary>
    public static async Task<ProjectAccess> LoadAsync(BackendDBContext context, RecordAccessScope scope)
    {
        // 管理者也要查自己所屬的團隊：新專案的主責預設帶入建立者的團隊，管理者也一樣。
        var teamIds = scope.UserId == 0
            ? []
            : await context.UserTeam
                .AsNoTracking()
                .Where(x => x.MyUserId == scope.UserId)
                .Select(x => x.TeamId)
                .ToListAsync();

        if (scope.IsAdmin)
        {
            return new ProjectAccess(true, scope.UserId, teamIds, []);
        }

        // 主責或協作團隊與自己的團隊有交集。沒有「公開」專案：沒掛團隊的專案只有管理者看得到。
        var projectIds = await context.Project
            .AsNoTracking()
            .Where(p => p.Teams.Any(t => teamIds.Contains(t.TeamId)))
            .Select(p => p.Id)
            .ToListAsync();

        return new ProjectAccess(false, scope.UserId, teamIds, projectIds);
    }
}
