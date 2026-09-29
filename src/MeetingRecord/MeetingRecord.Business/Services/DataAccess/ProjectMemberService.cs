using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>專案成員清單的一列。</summary>
public sealed record ProjectMemberItem(int UserId, string Name, string Account, ProjectMemberRole Role)
{
    public string RoleText => Role == ProjectMemberRole.Owner ? "負責人" : "協作者";
}

/// <summary>可以被加進專案的使用者（啟用中的帳號）。</summary>
public sealed record AssignableUser(int Id, string Name, string Account);

/// <summary>
/// 專案成員維護（0.4.99）。兩個入口共用同一份 <c>ProjectMember</c> 名單：
/// <list type="bullet">
/// <item>專案頁的「成員」對話框：負責人或管理者加減協作者，管理者轉移負責人。</item>
/// <item>使用者管理：管理者替某個人勾選他參與的專案，勾了就是協作者。</item>
/// </list>
/// <para>
/// ⚠️ 負責人**一個專案只有一位**，只能經 <see cref="SetOwnerAsync"/> 轉移；
/// 其他方法都不會新增或移除負責人列，<c>Project.Owner</c> 的顯示字串也只在那裡同步。
/// </para>
/// </summary>
public class ProjectMemberService
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;
    private readonly ILogger<ProjectMemberService> logger;

    public ProjectMemberService(
        BackendDBContext context,
        ProjectAccessService projectAccess,
        ILogger<ProjectMemberService> logger)
    {
        this.context = context;
        this.projectAccess = projectAccess;
        this.logger = logger;
    }

    /// <summary>專案成員名單，負責人排第一。看不到這個專案時回空清單。</summary>
    public async Task<List<ProjectMemberItem>> GetMembersAsync(int projectId)
    {
        if (!(await projectAccess.GetAsync()).CanViewProject(projectId))
        {
            return [];
        }

        return await context.ProjectMember
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Role)
            .ThenBy(x => x.MyUser!.Name)
            .Select(x => new ProjectMemberItem(x.MyUserId, x.MyUser!.Name, x.MyUser.Account, x.Role))
            .ToListAsync();
    }

    public async Task<List<AssignableUser>> GetAssignableUsersAsync()
        => await context.MyUser
            .AsNoTracking()
            .Where(x => x.Status)
            .OrderBy(x => x.Name)
            .Select(x => new AssignableUser(x.Id, x.Name, x.Account))
            .ToListAsync();

    public async Task<VerifyRecordResult> AddCollaboratorAsync(int projectId, int userId)
    {
        if (!(await projectAccess.GetAsync()).CanManageProject(projectId))
        {
            return VerifyRecordResultFactory.Build(false, "只有專案負責人或管理者可以管理成員。");
        }

        if (await context.ProjectMember.AnyAsync(x => x.ProjectId == projectId && x.MyUserId == userId))
        {
            return VerifyRecordResultFactory.Build(false, "這個人已經是專案成員。");
        }

        if (!await context.MyUser.AnyAsync(x => x.Id == userId && x.Status))
        {
            return VerifyRecordResultFactory.Build(false, "找不到這個使用者，或帳號已停用。");
        }

        context.ProjectMember.Add(new ProjectMember
        {
            ProjectId = projectId,
            MyUserId = userId,
            Role = ProjectMemberRole.Collaborator,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        logger.LogInformation("Collaborator added. ProjectId={ProjectId}, UserId={UserId}", projectId, userId);
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> RemoveCollaboratorAsync(int projectId, int userId)
    {
        if (!(await projectAccess.GetAsync()).CanManageProject(projectId))
        {
            return VerifyRecordResultFactory.Build(false, "只有專案負責人或管理者可以管理成員。");
        }

        var member = await context.ProjectMember
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.MyUserId == userId);
        if (member is null)
        {
            return VerifyRecordResultFactory.Build(false, "這個人不是專案成員。");
        }

        if (member.Role == ProjectMemberRole.Owner)
        {
            return VerifyRecordResultFactory.Build(false, "負責人不能移除；請由管理者先把負責人轉給別人。");
        }

        context.ProjectMember.Remove(member);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        logger.LogInformation("Collaborator removed. ProjectId={ProjectId}, UserId={UserId}", projectId, userId);
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>
    /// 轉移負責人，只有管理者能做。原負責人降為協作者（不是移出專案——他多半還要繼續看），
    /// 新負責人原本不是成員的話直接加入。<c>Project.Owner</c> 顯示字串一起同步。
    /// </summary>
    public async Task<VerifyRecordResult> SetOwnerAsync(int projectId, int userId)
    {
        if (!(await projectAccess.GetAsync()).IsAdmin)
        {
            return VerifyRecordResultFactory.Build(false, "只有管理者可以轉移負責人。");
        }

        var project = await context.Project.FirstOrDefaultAsync(x => x.Id == projectId);
        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId && x.Status);
        if (project is null || user is null)
        {
            return VerifyRecordResultFactory.Build(false, "找不到專案或使用者，或帳號已停用。");
        }

        var members = await context.ProjectMember.Where(x => x.ProjectId == projectId).ToListAsync();
        foreach (var owner in members.Where(x => x.Role == ProjectMemberRole.Owner && x.MyUserId != userId))
        {
            owner.Role = ProjectMemberRole.Collaborator;
        }

        var target = members.FirstOrDefault(x => x.MyUserId == userId);
        if (target is null)
        {
            context.ProjectMember.Add(new ProjectMember
            {
                ProjectId = projectId,
                MyUserId = userId,
                Role = ProjectMemberRole.Owner,
            });
        }
        else
        {
            target.Role = ProjectMemberRole.Owner;
        }

        project.Owner = user.Name;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        logger.LogInformation("Project owner changed. ProjectId={ProjectId}, UserId={UserId}", projectId, userId);
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>某個使用者參與的所有專案 Id（負責人與協作者都算），給使用者管理頁回填。</summary>
    public async Task<List<int>> GetUserProjectIdsAsync(int userId)
        => await context.ProjectMember
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Select(x => x.ProjectId)
            .ToListAsync();

    /// <summary>某個使用者擔任負責人的專案 Id。使用者管理頁用來鎖住這些選項（負責人不能在那裡取消）。</summary>
    public async Task<List<int>> GetUserOwnedProjectIdsAsync(int userId)
        => await context.ProjectMember
            .AsNoTracking()
            .Where(x => x.MyUserId == userId && x.Role == ProjectMemberRole.Owner)
            .Select(x => x.ProjectId)
            .ToListAsync();

    /// <summary>
    /// 使用者管理頁存檔：把這個人的協作專案同步成 <paramref name="projectIds"/>。只有管理者能做。
    /// **只增刪協作者列**——他擔任負責人的專案不在清單裡也不會被移除，負責人只能用 <see cref="SetOwnerAsync"/> 轉移。
    /// </summary>
    public async Task<VerifyRecordResult> SyncUserCollaborationsAsync(int userId, IEnumerable<int> projectIds)
    {
        if (!(await projectAccess.GetAsync()).IsAdmin)
        {
            return VerifyRecordResultFactory.Build(false, "只有管理者可以指派使用者的專案。");
        }

        var desired = projectIds.ToHashSet();
        var existingProjectIds = await context.Project
            .Where(x => desired.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync();
        desired.IntersectWith(existingProjectIds);

        var current = await context.ProjectMember.Where(x => x.MyUserId == userId).ToListAsync();

        foreach (var removed in current.Where(x => x.Role == ProjectMemberRole.Collaborator && !desired.Contains(x.ProjectId)))
        {
            context.ProjectMember.Remove(removed);
        }

        var currentIds = current.Select(x => x.ProjectId).ToHashSet();
        foreach (var projectId in desired.Where(id => !currentIds.Contains(id)))
        {
            context.ProjectMember.Add(new ProjectMember
            {
                ProjectId = projectId,
                MyUserId = userId,
                Role = ProjectMemberRole.Collaborator,
            });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return VerifyRecordResultFactory.Build(true);
    }
}
