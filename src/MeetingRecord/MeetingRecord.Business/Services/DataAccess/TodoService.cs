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

/// <summary>
/// 待辦事項的 Blazor 服務層。CRUD 骨架比照 <see cref="PromptTemplateService"/>。
///
/// 0.4.66 起**沒有列級權控**：分類與團隊欄位已徹底移除，所有待辦對所有使用者可見。
/// 待辦必定隸屬於專案，而專案自 0.4.39 起也已退出團隊控管，兩者一致。
/// </summary>
public class TodoService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<TodoService> Logger { get; }

    public TodoService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<TodoService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
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

        return await BeforeAddCheckAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(TodoAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for todo. TodoId={TodoId}, Title={TodoTitle}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    #endregion

    #region 負責人工作量

    /// <summary>未填負責人的待辦歸在這個假名下。與清單「負責人」欄的顯示文字一致。</summary>
    public const string UnassignedOwner = "未指定";

    /// <summary>
    /// 各負責人的工作量統計，供待辦事項頁右側面板使用。
    ///
    /// <para>
    /// <b>分組鍵一律 Trim 後在記憶體正規化</b>：<c>Owner</c> 是自由文字、沒有任何正規化，
    /// 交給 SQLite 的 GROUP BY 會把「陳大文」與「陳大文 」算成兩個人，
    /// null 與空字串也會變成兩組。
    /// </para>
    ///
    /// <para>
    /// 只投影三個純量欄位，不撈實體也不映射 AdapterModel——面板只需要數字，
    /// 撈全表實體再映射會比左邊的清單本身更貴。選中某人之後才用
    /// <see cref="GetByOwnerAsync"/> 撈他那一份。
    /// </para>
    ///
    /// <para>
    /// <paramref name="projectFilter"/> 刻意只接專案過濾，不接狀態與關鍵字：
    /// 這個面板顯示的就是狀態分布，再被狀態過濾一次會得到「每個人都 100%」
    /// 或「每個人都 0%」——那不是空資料，是看起來合理的錯誤數字。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<TodoOwnerSummary>> GetOwnerSummariesAsync(
        int? projectFilter,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Todo> query = context.Todo.AsNoTracking();
        if (projectFilter is > 0)
        {
            query = query.Where(x => x.ProjectId == projectFilter);
        }

        var rows = await query
            .Select(x => new { x.Owner, x.Status, x.DueDate })
            .ToListAsync(cancellationToken);

        var today = DateTime.Today;
        var completed = TodoAdapterModel.CompletedStatus;
        var inProgress = TodoAdapterModel.StatusOptions[1];
        var pending = TodoAdapterModel.StatusOptions[0];

        var summaries = rows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Owner) ? UnassignedOwner : x.Owner.Trim())
            .Select(group => new TodoOwnerSummary(
                group.Key,
                group.Count(),
                group.Count(x => x.Status == completed),
                group.Count(x => x.Status == inProgress),
                group.Count(x => x.Status == pending),
                // 逾期只算未完成的，與 TodoAdapterModel.IsOverdue 的語意一致。
                group.Count(x => x.Status != completed
                              && x.DueDate.HasValue
                              && x.DueDate.Value.Date < today)))
            .OrderByDescending(x => x.Total)
            .ThenBy(x => x.Owner, StringComparer.Ordinal)
            .ToList();

        Logger.LogDebug(
            "Loaded todo owner summaries. ProjectFilter={ProjectFilter}, OwnerCount={OwnerCount}",
            projectFilter, summaries.Count);

        return summaries;
    }

    /// <summary>
    /// 某一位負責人的待辦清單。刻意不分頁——單一個人的待辦量級不需要。
    /// 排序與清單頁預設一致（有截止日的在前、近的在前）。
    /// </summary>
    public async Task<IReadOnlyList<TodoAdapterModel>> GetByOwnerAsync(
        string owner,
        int? projectFilter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return [];
        }

        IQueryable<Todo> query = context.Todo.AsNoTracking().Include(x => x.Project);
        if (projectFilter is > 0)
        {
            query = query.Where(x => x.ProjectId == projectFilter);
        }

        // 比對前先 Trim，與 GetOwnerSummariesAsync 的分組鍵用同一個規則。
        query = owner == UnassignedOwner
            ? query.Where(x => x.Owner == null || x.Owner.Trim() == string.Empty)
            : query.Where(x => x.Owner != null && x.Owner.Trim() == owner);

        var records = await query
            .OrderBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return Mapper.Map<List<TodoAdapterModel>>(records);
    }

    #endregion
}

/// <summary>
/// 右側負責人面板的一列統計。
/// 清單刻意不放在這裡——選中某人之後才用 <see cref="TodoService.GetByOwnerAsync"/> 另外撈。
/// </summary>
public sealed record TodoOwnerSummary(
    string Owner,
    int Total,
    int Completed,
    int InProgress,
    int Pending,
    int Overdue)
{
    /// <summary>
    /// 完成度（0～100）。
    ///
    /// 刻意用整數截斷而不是四捨五入：199/200 四捨五入會顯示 100%，
    /// 看起來像全做完了，是假資訊。
    /// </summary>
    public int CompletionPercent => Total == 0 ? 0 : Completed * 100 / Total;
}
