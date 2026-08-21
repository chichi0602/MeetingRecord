using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Business.Repositories;

/// <summary>
/// 會議紀錄提示詞的 Web API 資料存取。
///
/// 與 <c>ProjectRepository</c> 一致，API（repository）路徑不做團隊行級過濾；
/// 團隊可見性只在 Blazor 的 <c>PromptTemplateService</c> 生效。
/// </summary>
public class PromptTemplateRepository
{
    private readonly BackendDBContext context;

    public PromptTemplateRepository(BackendDBContext context)
    {
        this.context = context;
    }

    #region 查詢方法

    public async Task<PromptTemplate?> GetByIdAsync(int id)
    {
        return await context.PromptTemplate.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<PromptTemplate>> GetPagedAsync(PromptTemplateSearchRequestDto request)
    {
        var query = context.PromptTemplate.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                x.Content.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        // 內容（Content）為長文字，刻意不開放排序。
        query = request.SortBy?.ToLower() switch
        {
            "name" => request.SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
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

        return new PagedResult<PromptTemplate>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var query = context.PromptTemplate.Where(x => x.Name == name);
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<PromptTemplate> AddAsync(PromptTemplate promptTemplate)
    {
        promptTemplate.CreatedAt = DateTime.Now;
        promptTemplate.UpdatedAt = DateTime.Now;

        await context.PromptTemplate.AddAsync(promptTemplate);
        await context.SaveChangesAsync();

        return promptTemplate;
    }

    public async Task<bool> UpdateAsync(PromptTemplate promptTemplate)
    {
        var existing = await context.PromptTemplate.FindAsync(promptTemplate.Id);
        if (existing == null)
        {
            return false;
        }

        promptTemplate.UpdatedAt = DateTime.Now;
        promptTemplate.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(promptTemplate);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var promptTemplate = await context.PromptTemplate.FindAsync(id);
        if (promptTemplate == null)
        {
            return false;
        }

        context.PromptTemplate.Remove(promptTemplate);
        await context.SaveChangesAsync();

        return true;
    }

    #endregion
}
