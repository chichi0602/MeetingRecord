using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Business.Repositories;

public class TeamRepository
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;

    public TeamRepository(BackendDBContext context, ProjectAccessService projectAccess)
    {
        this.context = context;
        this.projectAccess = projectAccess;
    }

    #region 查詢方法

    public async Task<Team?> GetByIdAsync(int id)
    {
        return await context.Team.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Team>> GetPagedAsync(TeamSearchRequestDto request)
    {
        var query = context.Team.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                (x.Code != null && x.Code.Contains(request.Keyword)) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        query = request.SortBy?.ToLower() switch
        {
            "name" => request.SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
            "code" => request.SortDescending ? query.OrderByDescending(x => x.Code) : query.OrderBy(x => x.Code),
            "isenabled" => request.SortDescending ? query.OrderByDescending(x => x.IsEnabled) : query.OrderBy(x => x.IsEnabled),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<Team>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var query = context.Team.Where(x => x.Name == name);
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    public async Task<bool> ExistsByCodeAsync(string code, int? excludeId = null)
    {
        var query = context.Team.Where(x => x.Code == code);
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Team> AddAsync(Team team)
    {
        team.CreatedAt = DateTime.Now;
        team.UpdatedAt = DateTime.Now;

        await context.Team.AddAsync(team);
        await context.SaveChangesAsync();

        return team;
    }

    public async Task<bool> UpdateAsync(Team team)
    {
        var existing = await context.Team.FindAsync(team.Id);
        if (existing == null)
        {
            return false;
        }

        team.UpdatedAt = DateTime.Now;
        team.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(team);
        await context.SaveChangesAsync();

        return true;
    }

    /// <summary>刪除團隊。還是某個專案的主責時，非管理者不刪、回傳錯誤訊息（0.4.102）；管理者不受限（0.4.104）。</summary>
    public async Task<(bool Found, string? Error)> DeleteAsync(int id)
    {
        var team = await context.Team.FindAsync(id);
        if (team == null)
        {
            return (false, null);
        }

        var primaryOf = (await projectAccess.GetAsync()).IsAdmin
            ? []
            : await ProjectTeamWriter.PrimaryProjectTitlesAsync(context, id);
        if (primaryOf.Count > 0)
        {
            return (true, ProjectTeamWriter.PrimaryInUseMessage(primaryOf));
        }

        context.Team.Remove(team);
        await context.SaveChangesAsync();

        return (true, null);
    }

    #endregion
}
