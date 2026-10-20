using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Repositories;

/// <summary>
/// 待辦事項的 Web API 資料存取。
///
/// 0.4.99 起與 Blazor 的 <c>TodoService</c> 套同一條專案權限：只看得到、改得到自己是成員的專案底下的待辦。
/// 看不到的一律當成不存在（控制器回 404／「專案不存在」）。
/// </summary>
public class TodoRepository
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;
    private readonly IAuditLogService auditLogService;
    private readonly TodoChangeRecorder changeRecorder;

    public TodoRepository(
        BackendDBContext context,
        ProjectAccessService projectAccess,
        IAuditLogService auditLogService,
        TodoChangeRecorder changeRecorder)
    {
        this.changeRecorder = changeRecorder;
        this.context = context;
        this.projectAccess = projectAccess;
        this.auditLogService = auditLogService;
    }

    #region 查詢方法

    public async Task<Todo?> GetByIdAsync(int id)
    {
        var access = await projectAccess.GetAsync();
        var todo = await access.Filter(context.Todo.AsNoTracking())
            .Include(x => x.Project)
            .Include(x => x.Meeting)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (todo is not null)
        {
            access.HideInvisibleSourceMeetings([todo]);
        }

        return todo;
    }

    public async Task<PagedResult<Todo>> GetPagedAsync(TodoSearchRequestDto request)
    {
        var query = (await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking())
            .Include(x => x.Project)
            .Include(x => x.Meeting)
            .AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Title.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)) ||
                (x.Owner != null && x.Owner.Contains(request.Keyword)));
        }

        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId.Value);
        }

        if (!string.IsNullOrEmpty(request.Status))
        {
            query = query.Where(x => x.Status == request.Status);
        }

        if (!string.IsNullOrEmpty(request.Priority))
        {
            query = query.Where(x => x.Priority == request.Priority);
        }

        query = request.SortBy?.ToLower() switch
        {
            "title" => request.SortDescending ? query.OrderByDescending(x => x.Title) : query.OrderBy(x => x.Title),
            "duedate" => request.SortDescending ? query.OrderByDescending(x => x.DueDate) : query.OrderBy(x => x.DueDate),
            "priority" => request.SortDescending ? query.OrderByDescending(x => x.Priority) : query.OrderBy(x => x.Priority),
            "status" => request.SortDescending ? query.OrderByDescending(x => x.Status) : query.OrderBy(x => x.Status),
            "owner" => request.SortDescending ? query.OrderByDescending(x => x.Owner) : query.OrderBy(x => x.Owner),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();
        (await projectAccess.GetAsync()).HideInvisibleSourceMeetings(items);

        return new PagedResult<Todo>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ProjectExistsAsync(int projectId)
    {
        // 看不到的專案當成不存在：新增與修改都靠這支擋「把待辦塞進別人的專案」。
        return (await projectAccess.GetAsync()).CanViewProject(projectId)
            && await context.Project.AnyAsync(x => x.Id == projectId);
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Todo> AddAsync(Todo todo)
    {
        // 一律由資料庫配號（0.4.115）：照抄用戶端的 Id 會撞主鍵回 500，可以拿來試探哪些 Id 存在。
        todo.Id = 0;
        todo.CreatedAt = DateTime.Now;
        todo.UpdatedAt = DateTime.Now;

        var access = await projectAccess.GetAsync();
        if (!access.CanViewProject(todo.ProjectId))
        {
            if (await context.Project.AnyAsync(x => x.Id == todo.ProjectId))
            {
                await AuditDeniedAsync(nameof(Project), todo.ProjectId, "TodoCreate");
            }

            throw new InvalidOperationException("找不到指定的專案項目。");
        }

        // 來源會議也要檢查（0.4.114）：要看得到、而且屬於同一個專案。原本只檢查專案，
        // 用戶端可以把看不到的會議掛上來，之後讀回的 MeetingTitle 就洩漏了那場會議的標題。
        if (todo.MeetingId is { } meetingId)
        {
            var meeting = await context.Meeting.AsNoTracking()
                .Where(x => x.Id == meetingId)
                .Select(x => new { x.ProjectId, x.CreatedByUserId })
                .FirstOrDefaultAsync();
            if (meeting is null
                || !access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId)
                || meeting.ProjectId != todo.ProjectId)
            {
                throw new InvalidOperationException("找不到指定的來源會議，或它不屬於這個專案。");
            }
        }

        // 1.0.46：已完成（封存）的專案唯讀（管理者不受限），控制器回 400。
        if (await ProjectArchiveRules.CheckWritableAsync(context, todo.ProjectId, access) is { } archivedError)
        {
            throw new InvalidOperationException(archivedError);
        }

        // 子項目（1.0.45）：與畫面同一份規則；違規丟 InvalidOperationException，控制器回 400。
        if (await TodoHierarchyRules.ValidateAsync(context, null, todo.ProjectId, todo.ParentTodoId) is { } hierarchyError)
        {
            throw new InvalidOperationException(hierarchyError);
        }

        await context.Todo.AddAsync(todo);
        if (todo.ParentTodoId is { } parentId)
        {
            await changeRecorder.StageSubtaskAsync(parentId, todo.ProjectId, todo.Title, added: true, AuditVia.Api);
        }

        await context.SaveChangesAsync();
        changeRecorder.Publish([]);

        await AuditAsync(AuditActions.TodoCreate, todo.Id, $"title={todo.Title}; projectId={todo.ProjectId}; via={AuditVia.Api}");
        return todo;
    }

    public async Task<bool> UpdateAsync(Todo todo)
    {
        var existing = await context.Todo.FindAsync(todo.Id);
        var access = await projectAccess.GetAsync();
        if (existing == null)
        {
            return false;
        }

        if (!access.CanViewProject(existing.ProjectId) || !access.CanViewProject(todo.ProjectId))
        {
            // 對外仍當成不存在（404），稽核另外記下（1.0.34）。
            await AuditDeniedAsync(nameof(Todo), todo.Id, "Update");
            return false;
        }

        // 1.0.46：已完成（封存）的專案唯讀（管理者不受限）；搬進封存專案也不行。控制器回 400。
        if ((await ProjectArchiveRules.CheckWritableAsync(context, existing.ProjectId, access) ?? await ProjectArchiveRules.CheckWritableAsync(context, todo.ProjectId, access)) is { } archivedError)
        {
            throw new InvalidOperationException(archivedError);
        }

        // 子項目（1.0.45）：與畫面同一份規則；違規丟 InvalidOperationException，控制器回 400。
        if (await TodoHierarchyRules.ValidateAsync(context, existing.Id, todo.ProjectId, todo.ParentTodoId) is { } hierarchyError)
        {
            throw new InvalidOperationException(hierarchyError);
        }

        var previousParentId = existing.ParentTodoId;
        var previousProjectId = existing.ProjectId;

        // 1.0.44：SetValues 會蓋掉 existing，先留一份舊值給異動紀錄（API 改的也要讓專案成員知道）。
        var before = new Todo
        {
            Id = existing.Id,
            ProjectId = existing.ProjectId,
            Title = existing.Title,
            DueDate = existing.DueDate,
            Owner = existing.Owner,
            Status = existing.Status,
            Priority = existing.Priority,
        };

        todo.UpdatedAt = DateTime.Now;
        todo.CreatedAt = existing.CreatedAt;
        // 來源會議紀錄由系統寫入，不開放 API 用戶端覆寫；搬到別的專案時清掉（0.4.115）。
        todo.MeetingId = existing.ProjectId == todo.ProjectId ? existing.MeetingId : null;

        context.Entry(existing).CurrentValues.SetValues(todo);
        var recipients = await changeRecorder.StageAsync(before, existing, AuditVia.Api);
        if (previousParentId != existing.ParentTodoId)
        {
            if (previousParentId is { } oldParent)
            {
                await changeRecorder.StageSubtaskAsync(oldParent, previousProjectId, existing.Title, added: false, AuditVia.Api);
            }

            if (existing.ParentTodoId is { } newParent)
            {
                await changeRecorder.StageSubtaskAsync(newParent, existing.ProjectId, existing.Title, added: true, AuditVia.Api);
            }
        }

        await context.SaveChangesAsync();
        changeRecorder.Publish(recipients);

        // 主待辦搬到別的專案時，子項目跟著搬。
        if (previousProjectId != existing.ProjectId)
        {
            await TodoHierarchyRules.MoveChildrenAsync(context, existing.Id, existing.ProjectId);
        }

        var moved = previousProjectId != todo.ProjectId ? $"; movedFrom={previousProjectId}" : string.Empty;
        await AuditAsync(AuditActions.TodoUpdate, todo.Id, $"title={todo.Title}; projectId={todo.ProjectId}{moved}; via={AuditVia.Api}");
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var todo = await context.Todo.FindAsync(id);
        if (todo == null)
        {
            return false;
        }

        if (!(await projectAccess.GetAsync()).CanViewProject(todo.ProjectId))
        {
            await AuditDeniedAsync(nameof(Todo), id, "Delete");
            return false;
        }

        // 1.0.46：已完成（封存）的專案唯讀（管理者不受限），控制器回 400。
        if (await ProjectArchiveRules.CheckWritableAsync(context, todo.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
        {
            throw new InvalidOperationException(archivedError);
        }

        // 1.0.52：刪除待辦只限專案負責人與管理者，控制器回 400。
        if (!(await projectAccess.GetAsync()).IsProjectOwner(todo.ProjectId))
        {
            throw new InvalidOperationException(ProjectOwnerRules.DeleteTodoMessage);
        }

        context.Todo.Remove(todo);
        await context.SaveChangesAsync();

        await AuditAsync(AuditActions.TodoDelete, id, $"title={todo.Title}; projectId={todo.ProjectId}; via={AuditVia.Api}");
        return true;
    }

    private Task AuditAsync(string action, int todoId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: nameof(Todo), targetId: todoId.ToString(), detail: detail);

    private Task AuditDeniedAsync(string targetType, int targetId, string operation)
        => auditLogService.WriteForCurrentUserAsync(
            AuditActions.PermissionDenied, success: false, targetType: targetType, targetId: targetId.ToString(),
            detail: $"reason=DataScope; op={operation}; via={AuditVia.Api}");

    #endregion
}
