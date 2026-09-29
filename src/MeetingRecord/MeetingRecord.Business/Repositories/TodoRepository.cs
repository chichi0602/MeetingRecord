using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;

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

    public TodoRepository(BackendDBContext context, ProjectAccessService projectAccess)
    {
        this.context = context;
        this.projectAccess = projectAccess;
    }

    #region 查詢方法

    public async Task<Todo?> GetByIdAsync(int id)
    {
        return await (await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking())
            .Include(x => x.Project)
            .Include(x => x.Meeting)
            .FirstOrDefaultAsync(x => x.Id == id);
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
        todo.CreatedAt = DateTime.Now;
        todo.UpdatedAt = DateTime.Now;

        if (!(await projectAccess.GetAsync()).CanViewProject(todo.ProjectId))
        {
            throw new InvalidOperationException("找不到指定的專案項目。");
        }

        await context.Todo.AddAsync(todo);
        await context.SaveChangesAsync();

        return todo;
    }

    public async Task<bool> UpdateAsync(Todo todo)
    {
        var existing = await context.Todo.FindAsync(todo.Id);
        var access = await projectAccess.GetAsync();
        if (existing == null || !access.CanViewProject(existing.ProjectId) || !access.CanViewProject(todo.ProjectId))
        {
            return false;
        }

        todo.UpdatedAt = DateTime.Now;
        todo.CreatedAt = existing.CreatedAt;
        // 來源會議紀錄由系統寫入，不開放 API 用戶端覆寫。
        todo.MeetingId = existing.MeetingId;

        context.Entry(existing).CurrentValues.SetValues(todo);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var todo = await context.Todo.FindAsync(id);
        if (todo == null || !(await projectAccess.GetAsync()).CanViewProject(todo.ProjectId))
        {
            return false;
        }

        context.Todo.Remove(todo);
        await context.SaveChangesAsync();

        return true;
    }

    #endregion
}
