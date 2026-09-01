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

/// <summary>
/// 待辦事項的 Blazor 服務層。CRUD 骨架與團隊列級權控比照 <see cref="PromptTemplateService"/>。
/// </summary>
public class TodoService
{
    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;

    public IMapper Mapper { get; }
    public ILogger<TodoService> Logger { get; }

    public TodoService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<TodoService> logger,
        IRecordAccessScopeProvider accessScope)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
    }

    #region 查詢

    public async Task<DataRequestResult<TodoAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading todos. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<TodoAdapterModel> result = new();
        IQueryable<Todo> dataSource = context.Todo.AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Meeting);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                (x.Owner ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                (x.Project != null && x.Project.Title.Contains(search)));
        }

        if (dataRequest.ProjectFilter is > 0)
        {
            dataSource = dataSource.Where(x => x.ProjectId == dataRequest.ProjectFilter);
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.StatusFilter))
        {
            dataSource = dataSource.Where(x => x.Status == dataRequest.StatusFilter);
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Todo>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Todo>(x => x.Teams, dataRequest.TeamFilters));
        }

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Todo>(x => x.Teams, scope.Teams));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(TodoAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.ProjectTitle))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Project!.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Project!.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.Owner))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.DueDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.DueDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.DueDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.Priority))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.Status))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(TodoAdapterModel.UpdatedAt))
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
            // 預設把「有截止日的排前面、越早到期越前面」，未指定截止日的墊底。
            dataSource = dataSource
                .OrderBy(x => x.DueDate == null)
                .ThenBy(x => x.DueDate)
                .ThenByDescending(x => x.Id);
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<Todo> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<TodoAdapterModel>>(records);
        Logger.LogDebug("Loaded todos successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<TodoAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading todo by id. TodoId={TodoId}", id);

        Todo? item = await context.Todo
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Meeting)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Todo not found. TodoId={TodoId}", id);
            return new TodoAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Todo access denied by team scope. TodoId={TodoId}", id);
            return new TodoAdapterModel();
        }

        return Mapper.Map<TodoAdapterModel>(item);
    }

    #endregion

    #region 新增 / 修改 / 刪除

    public async Task<VerifyRecordResult> AddAsync(TodoAdapterModel paraObject)
    {
        Logger.LogInformation("Creating todo. Title={TodoTitle}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo itemParameter = Mapper.Map<Todo>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Todo.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);

            Logger.LogInformation("Todo created successfully. TodoId={TodoId}, Title={TodoTitle}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create todo. Title={TodoTitle}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增待辦事項失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(TodoAdapterModel paraObject)
    {
        Logger.LogInformation("Updating todo. TodoId={TodoId}, Title={TodoTitle}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo? item = await context.Todo
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Todo update rejected because record was not found. TodoId={TodoId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的待辦事項。");
            }

            Todo itemData = Mapper.Map<Todo>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;
            // 來源會議紀錄不開放從畫面修改，一律沿用既有值。
            itemData.MeetingId = item.MeetingId;

            CleanTrackingHelper.Clean<Todo>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);

            Logger.LogInformation("Todo updated successfully. TodoId={TodoId}, Title={TodoTitle}", itemData.Id, itemData.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update todo. TodoId={TodoId}, Title={TodoTitle}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改待辦事項失敗。", ex);
        }
    }

    /// <summary>
    /// 切換完成狀態（清單上的勾選框）。完成與否只寫 Status 一個欄位。
    /// </summary>
    public async Task<VerifyRecordResult> SetCompletedAsync(int id, bool isCompleted)
    {
        Logger.LogInformation("Setting todo completion. TodoId={TodoId}, IsCompleted={IsCompleted}", id, isCompleted);

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo? item = await context.Todo.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要更新的待辦事項。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Todo completion denied by team scope. TodoId={TodoId}", id);
                return VerifyRecordResultFactory.Build(false, "沒有權限更新這筆待辦事項。");
            }

            // 取消完成時退回「進行中」而非「待辦」——已經動過的事情退回未開始並不合理。
            item.Status = isCompleted
                ? TodoAdapterModel.CompletedStatus
                : TodoAdapterModel.StatusOptions[1];
            item.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set todo completion. TodoId={TodoId}", id);
            return VerifyRecordResultFactory.Build(false, "更新完成狀態失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting todo. TodoId={TodoId}", id);

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo? item = await context.Todo
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Todo deletion rejected because record was not found. TodoId={TodoId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的待辦事項。");
            }

            CleanTrackingHelper.Clean<Todo>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);

            Logger.LogInformation("Todo deleted successfully. TodoId={TodoId}, Title={TodoTitle}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete todo. TodoId={TodoId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除待辦事項失敗。", ex);
        }
    }

    #endregion

    #region 前置檢查

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(TodoAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for todo. Title={TodoTitle}", paraObject.Title);

        var project = await context.Project.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.ProjectId);

        if (project is null)
        {
            Logger.LogWarning("Pre-create validation failed because project was not found. ProjectId={ProjectId}", paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(project.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Pre-create validation denied by project team scope. ProjectId={ProjectId}", paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "沒有權限在這個專案下新增待辦事項。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(TodoAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for todo. TodoId={TodoId}", paraObject.Id);

        CleanTrackingHelper.Clean<Todo>(context);
        var searchItem = await context.Todo
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because todo was not found. TodoId={TodoId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的待辦事項不存在。");
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(searchItem.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Pre-update validation denied by team scope. TodoId={TodoId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆待辦事項。");
        }

        return await BeforeAddCheckAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(TodoAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for todo. TodoId={TodoId}, Title={TodoTitle}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    #endregion
}
