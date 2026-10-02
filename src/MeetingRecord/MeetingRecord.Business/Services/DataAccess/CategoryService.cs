using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

public class CategoryService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<CategoryService> Logger { get; }

    public CategoryService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<CategoryService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
    }

    public async Task<DataRequestResult<CategoryAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading categories. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<CategoryAdapterModel> result = new();
        IQueryable<Category> dataSource = context.Category.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(CategoryAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(CategoryAdapterModel.IsEnabled))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(CategoryAdapterModel.UpdatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }
        else
        {
            dataSource = dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<Category> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<CategoryAdapterModel>>(records);

        var categoryIds = result.Result.Select(x => x.Id).ToList();
        var links = await context.CategoryTeam.AsNoTracking()
            .Where(x => categoryIds.Contains(x.CategoryId))
            .Select(x => new { x.CategoryId, x.TeamId, TeamName = x.Team!.Name })
            .ToListAsync();
        foreach (var item in result.Result)
        {
            var mine = links.Where(x => x.CategoryId == item.Id).OrderBy(x => x.TeamName).ToList();
            item.TeamIds = mine.Select(x => x.TeamId).ToList();
            item.TeamNames = mine.Select(x => x.TeamName).ToList();
        }
        Logger.LogDebug("Loaded categories successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<CategoryAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading category by id. CategoryId={CategoryId}", id);

        Category? item = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Category not found. CategoryId={CategoryId}", id);
            return new CategoryAdapterModel();
        }

        return Mapper.Map<CategoryAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogInformation("Creating category. Name={CategoryName}", paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<Category>(context);
            Category itemParameter = Mapper.Map<Category>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Category.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            paraObject.Id = itemParameter.Id;
            CleanTrackingHelper.Clean<Category>(context);

            Logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={CategoryName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create category. Name={CategoryName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogInformation("Updating category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<Category>(context);
            Category? item = await context.Category
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Category update rejected because record was not found. CategoryId={CategoryId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的分類資料。");
            }

            Category itemData = Mapper.Map<Category>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            CleanTrackingHelper.Clean<Category>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Category>(context);

            Logger.LogInformation("Category updated successfully. CategoryId={CategoryId}, Name={CategoryName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting category. CategoryId={CategoryId}", id);

        try
        {
            CleanTrackingHelper.Clean<Category>(context);
            Category? item = await context.Category
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Category deletion rejected because record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的分類資料。");
            }

            CleanTrackingHelper.Clean<Category>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Category>(context);

            Logger.LogInformation("Category deleted successfully. CategoryId={CategoryId}, Name={CategoryName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete category. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for category. Name={CategoryName}", paraObject.Name);

        var name = (paraObject.Name ?? string.Empty).Trim();
        var searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because category name already exists. Name={CategoryName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "分類名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);

        CleanTrackingHelper.Clean<Category>(context);
        var searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because category was not found. CategoryId={CategoryId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的分類資料不存在。");
        }

        var name = (paraObject.Name ?? string.Empty).Trim();
        searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because category name already exists. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "分類名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 清單上點狀態膠囊直接切換啟用狀態（0.4.105），不必進編輯視窗。
    /// </summary>
    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool value)
    {
        Logger.LogInformation("Setting Category state. Id={Id}, Value={Value}", id, value);

        try
        {
            CleanTrackingHelper.Clean<Category>(context);

            // 刻意不加 AsNoTracking：這裡要靠變更追蹤把欄位寫回去。
            Category? item = await context.Category.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                Logger.LogWarning("Category state update rejected because record was not found. Id={Id}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要更新的分類資料。");
            }

            item.IsEnabled = value;
            item.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Category>(context);

            Logger.LogInformation("Category state updated. Id={Id}, Value={Value}", id, value);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set Category state. Id={Id}", id);
            return VerifyRecordResultFactory.Build(false, "更新。", ex);
        }
    }

    /// <summary>「負責團隊（參考）」多選的選項：啟用中的團隊。</summary>
    public async Task<List<TeamOption>> GetSelectableTeamsAsync()
    {
        return await context.Team.AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => new TeamOption(x.Id, x.Name))
            .ToListAsync();
    }

    /// <summary>把分類的負責團隊同步成 <paramref name="teamIds"/>（只是參考資訊，不影響可見性）。</summary>
    public async Task SyncTeamsAsync(int categoryId, IEnumerable<int> teamIds)
    {
        var wanted = teamIds.Distinct().ToList();
        var validIds = await context.Team.Where(x => wanted.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        var rows = await context.CategoryTeam.Where(x => x.CategoryId == categoryId).ToListAsync();

        context.CategoryTeam.RemoveRange(rows.Where(x => !validIds.Contains(x.TeamId)));
        var existing = rows.Select(x => x.TeamId).ToHashSet();
        context.CategoryTeam.AddRange(validIds.Where(id => !existing.Contains(id))
            .Select(id => new CategoryTeam { CategoryId = categoryId, TeamId = id }));
        await context.SaveChangesAsync();
        CleanTrackingHelper.Clean<CategoryTeam>(context);
    }

    public sealed record TeamOption(int Id, string Name);

    /// <summary>
    /// 取得所有啟用中的分類名稱（依名稱排序），供其他頁面下拉選取使用。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        return await context.Category
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }
}
