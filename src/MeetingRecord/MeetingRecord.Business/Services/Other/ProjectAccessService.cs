using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 目前使用者的專案存取範圍（0.4.99）。管理者 <see cref="IsAdmin"/> 為 true 時不受任何限制，
/// 其餘清單可忽略。
/// </summary>
/// <param name="IsAdmin">管理者（<c>MyUser.IsAdmin</c>）。</param>
/// <param name="UserId">使用者 Id；解析不到時為 0，這時什麼都看不到。</param>
/// <param name="ProjectIds">是成員（負責人或協作者）的專案。</param>
/// <param name="OwnedProjectIds">是負責人的專案，<see cref="ProjectIds"/> 的子集。</param>
public sealed record ProjectAccess(
    bool IsAdmin,
    int UserId,
    IReadOnlyList<int> ProjectIds,
    IReadOnlyList<int> OwnedProjectIds)
{
    public bool CanViewProject(int projectId) => IsAdmin || ProjectIds.Contains(projectId);

    /// <summary>編輯專案資料、加減協作者：負責人或管理者。刪除專案不在這裡，只有管理者能做。</summary>
    public bool CanManageProject(int projectId) => IsAdmin || OwnedProjectIds.Contains(projectId);

    /// <summary>
    /// 會議看得到的條件：已歸屬專案 → 看專案成員；**未歸屬 → 只有上傳者**。
    /// 上傳者為 null（0.4.80 以前、回填推不回來的舊會議）時只有管理者看得到。
    /// </summary>
    public bool CanViewMeeting(int? projectId, int? createdByUserId)
        => IsAdmin
            || (projectId is { } id
                ? ProjectIds.Contains(id)
                : UserId != 0 && createdByUserId == UserId);

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

/// <summary>
/// 專案層級資料權限的**唯一入口**（0.4.99）。專案、會議、待辦、附件、AI 問答的每一條讀寫路徑
/// 都要經過這裡取得 <see cref="ProjectAccess"/>，不要在各服務自己查 <c>ProjectMember</c>。
///
/// <para>
/// 刻意不快取：Blazor 的 scope 跟著整條連線活很久，成員名單在這段期間可能被負責人改掉；
/// 一次查詢只撈兩欄，代價很小。
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
        if (scope.IsAdmin)
        {
            return new ProjectAccess(true, scope.UserId, [], []);
        }

        if (scope.UserId == 0)
        {
            return new ProjectAccess(false, 0, [], []);
        }

        var memberships = await context.ProjectMember
            .AsNoTracking()
            .Where(x => x.MyUserId == scope.UserId)
            .Select(x => new { x.ProjectId, x.Role })
            .ToListAsync();

        return new ProjectAccess(
            false,
            scope.UserId,
            [.. memberships.Select(x => x.ProjectId)],
            [.. memberships.Where(x => x.Role == ProjectMemberRole.Owner).Select(x => x.ProjectId)]);
    }
}
