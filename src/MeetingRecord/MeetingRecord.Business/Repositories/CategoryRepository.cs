using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Repositories;

public class CategoryRepository
{
    private readonly BackendDBContext context;
    private readonly IAuditLogService auditLogService;

    public CategoryRepository(BackendDBContext context, IAuditLogService auditLogService)
    {
        this.context = context;
        this.auditLogService = auditLogService;
    }

    #region 查詢方法

    public async Task<Category?> GetByIdAsync(int id)
    {
        return await context.Category.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Category>> GetPagedAsync(CategorySearchRequestDto request)
    {
        var query = context.Category.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

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

        return new PagedResult<Category>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var query = context.Category.Where(x => x.Name == name);
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Category> AddAsync(Category category)
    {
        category.CreatedAt = DateTime.Now;
        category.UpdatedAt = DateTime.Now;

        await context.Category.AddAsync(category);
        await context.SaveChangesAsync();

        await AuditAsync(AuditActions.CategoryCreate, category.Id, $"name={category.Name}; via={AuditVia.Api}");
        return category;
    }

    public async Task<bool> UpdateAsync(Category category)
    {
        var existing = await context.Category.FindAsync(category.Id);
        if (existing == null)
        {
            return false;
        }

        category.UpdatedAt = DateTime.Now;
        category.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(category);
        await context.SaveChangesAsync();

        await AuditAsync(AuditActions.CategoryUpdate, category.Id, $"name={existing.Name}; via={AuditVia.Api}");
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var category = await context.Category.FindAsync(id);
        if (category == null)
        {
            return false;
        }

        context.Category.Remove(category);
        await context.SaveChangesAsync();

        await AuditAsync(AuditActions.CategoryDelete, id, $"name={category.Name}; via={AuditVia.Api}");
        return true;
    }

    /// <summary>稽核（1.0.34）：操作者由 WriteForCurrentUserAsync 從 API 的 claims 取得。</summary>
    private Task AuditAsync(string action, int id, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: nameof(Category), targetId: id.ToString(), detail: detail);

    #endregion
}
