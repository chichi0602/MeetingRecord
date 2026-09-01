using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

public class PromptTemplateService
{
    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;

    public IMapper Mapper { get; }
    public ILogger<PromptTemplateService> Logger { get; }

    public PromptTemplateService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<PromptTemplateService> logger,
        IRecordAccessScopeProvider accessScope)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
    }

    public async Task<DataRequestResult<PromptTemplateAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading prompt templates. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<PromptTemplateAdapterModel> result = new();
        IQueryable<PromptTemplate> dataSource = context.PromptTemplate.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Name.Contains(search) ||
                x.Content.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<PromptTemplate>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<PromptTemplate>(x => x.Teams, dataRequest.TeamFilters));
        }

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<PromptTemplate>(x => x.Teams, scope.Teams));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            // 內容（Content）為長文字，刻意不開放排序。
            if (dataRequest.SortField == nameof(PromptTemplateAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(PromptTemplateAdapterModel.IsEnabled))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(PromptTemplateAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(PromptTemplateAdapterModel.UpdatedAt))
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

        List<PromptTemplate> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<PromptTemplateAdapterModel>>(records);
        Logger.LogDebug("Loaded prompt templates successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<PromptTemplateAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading prompt template by id. PromptTemplateId={PromptTemplateId}", id);

        PromptTemplate? item = await context.PromptTemplate
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Prompt template not found. PromptTemplateId={PromptTemplateId}", id);
            return new PromptTemplateAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Prompt template access denied by team scope. PromptTemplateId={PromptTemplateId}", id);
            return new PromptTemplateAdapterModel();
        }

        return Mapper.Map<PromptTemplateAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(PromptTemplateAdapterModel paraObject)
    {
        Logger.LogInformation("Creating prompt template. Name={PromptTemplateName}", paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<PromptTemplate>(context);
            PromptTemplate itemParameter = Mapper.Map<PromptTemplate>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.PromptTemplate.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<PromptTemplate>(context);

            Logger.LogInformation("Prompt template created successfully. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create prompt template. Name={PromptTemplateName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增提示詞失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(PromptTemplateAdapterModel paraObject)
    {
        Logger.LogInformation("Updating prompt template. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", paraObject.Id, paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<PromptTemplate>(context);
            PromptTemplate? item = await context.PromptTemplate
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Prompt template update rejected because record was not found. PromptTemplateId={PromptTemplateId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的提示詞資料。");
            }

            PromptTemplate itemData = Mapper.Map<PromptTemplate>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            CleanTrackingHelper.Clean<PromptTemplate>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<PromptTemplate>(context);

            Logger.LogInformation("Prompt template updated successfully. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update prompt template. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改提示詞失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting prompt template. PromptTemplateId={PromptTemplateId}", id);

        try
        {
            CleanTrackingHelper.Clean<PromptTemplate>(context);
            PromptTemplate? item = await context.PromptTemplate
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Prompt template deletion rejected because record was not found. PromptTemplateId={PromptTemplateId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的提示詞資料。");
            }

            CleanTrackingHelper.Clean<PromptTemplate>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<PromptTemplate>(context);

            Logger.LogInformation("Prompt template deleted successfully. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete prompt template. PromptTemplateId={PromptTemplateId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除提示詞失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(PromptTemplateAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for prompt template. Name={PromptTemplateName}", paraObject.Name);

        var name = (paraObject.Name ?? string.Empty).Trim();
        var searchItem = await context.PromptTemplate
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because prompt template name already exists. Name={PromptTemplateName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "提示詞名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(PromptTemplateAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for prompt template. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", paraObject.Id, paraObject.Name);

        CleanTrackingHelper.Clean<PromptTemplate>(context);
        var searchItem = await context.PromptTemplate
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because prompt template was not found. PromptTemplateId={PromptTemplateId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的提示詞資料不存在。");
        }

        var name = (paraObject.Name ?? string.Empty).Trim();
        searchItem = await context.PromptTemplate
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because prompt template name already exists. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "提示詞名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(PromptTemplateAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for prompt template. PromptTemplateId={PromptTemplateId}, Name={PromptTemplateName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 取得所有啟用中的提示詞名稱（依名稱排序），供其他頁面下拉選取使用。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        return await context.PromptTemplate
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }

    /// <summary>
    /// 取得啟用中且目前使用者可存取的提示詞，供「AI 轉會議紀錄」的下拉選用。
    ///
    /// 與 <see cref="GetAllEnabledNamesAsync"/> 的差別：這裡帶 Id（呼叫端要據以取內容）
    /// 並套用團隊列級權控——沒有權控的話會出現「選單看得到但讀不到內容」，
    /// 或讓使用者用到其他團隊的提示詞。
    /// </summary>
    public async Task<List<PromptTemplateAdapterModel>> GetEnabledSelectableAsync(
        CancellationToken cancellationToken = default)
    {
        IQueryable<PromptTemplate> dataSource = context.PromptTemplate
            .AsNoTracking()
            .Where(x => x.IsEnabled);

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(
                TagStringHelper.BuildTeamAccessPredicate<PromptTemplate>(x => x.Teams, scope.Teams));
        }

        var items = await dataSource
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Mapper.Map<List<PromptTemplateAdapterModel>>(items);
    }
}
