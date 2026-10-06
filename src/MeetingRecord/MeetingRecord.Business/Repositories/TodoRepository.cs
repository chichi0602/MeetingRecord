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
        // 來源會議紀錄由系統寫入，不開放 API 用戶端覆寫；搬到別的專案時清掉（0.4.115）。
        todo.MeetingId = existing.ProjectId == todo.ProjectId ? existing.MeetingId : null;

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
