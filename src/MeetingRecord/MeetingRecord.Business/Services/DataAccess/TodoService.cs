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
using MeetingRecord.Share.Helpers;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 待辦事項的 Blazor 服務層。CRUD 骨架比照 <see cref="PromptTemplateService"/>。
///
/// 0.4.99 起**跟著專案走**：只看得到、改得到自己是成員的專案底下的待辦（管理者不受限），
/// 判斷一律經 <see cref="ProjectAccessService"/>。0.4.66～0.4.98 期間所有待辦對所有人可見。
/// </summary>
public class TodoService
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;
    private readonly IAuditLogService auditLogService;
    private readonly TodoChangeRecorder changeRecorder;

    public IMapper Mapper { get; }
    public ILogger<TodoService> Logger { get; }

    public TodoService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<TodoService> logger,
        ProjectAccessService projectAccess,
        IAuditLogService auditLogService,
        TodoChangeRecorder changeRecorder)
    {
        this.context = context;
        this.projectAccess = projectAccess;
        this.auditLogService = auditLogService;
        this.changeRecorder = changeRecorder;
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
        var access = await projectAccess.GetAsync();
        IQueryable<Todo> dataSource = access.Filter(context.Todo.AsNoTracking())
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

        // 1.0.46：「使用中｜已完成｜全部」檢視（看待辦所屬專案是不是已完成）。
        dataSource = ProjectArchiveRules.ApplyScope(dataSource, dataRequest.ProjectScope);

        if (!string.IsNullOrWhiteSpace(dataRequest.StatusFilter))
        {
            dataSource = dataSource.Where(x => x.Status == dataRequest.StatusFilter);
        }

        // 1.0.45 子項目：只分頁主層，子項目跟著主待辦出現。篩選命中子項目時也要列出它的主待辦，
        // 否則「負責人是我的子項目」用搜尋找不到。matching 是套完篩選的全部待辦（主層與子項目都有）。
        var matching = dataSource;
        dataSource = access.Filter(context.Todo.AsNoTracking())
            .Include(x => x.Project)
            .Include(x => x.Meeting)
            .Where(t => t.ParentTodoId == null
                && (matching.Any(m => m.Id == t.Id) || matching.Any(m => m.ParentTodoId == t.Id)));

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

        // Take 刻意不再看 dataRequest.Take：呼叫端（清單頁）一律傳 0，原本的
        // `if (dataRequest.Take != 0)` 等於從來沒有分頁——第 N 頁會回「第 N 頁以後的全部資料」。
        // Count 是在 Skip/Take 之前算的，所以分頁器的總數本來就對，修好之後才第一次真的對上。
        // 註：其餘 6 支 *Service.GetAsync(DataRequest) 仍是舊寫法，見「開發慣例與限制速查」。
        dataSource = dataSource
            .Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize)
            .Take(dataRequest.PageSize);

        List<Todo> records = await dataSource.ToListAsync();
        access.HideInvisibleSourceMeetings(records);
        var models = Mapper.Map<List<TodoAdapterModel>>(records);
        await FillChildrenAsync(models, matching, access);
        await FillRecentDueDateChangesAsync(models.Concat(models.SelectMany(x => x.Children ?? [])).ToList());
        result.Result = models;
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
            .Include(x => x.Parent)
            .FirstOrDefaultAsync(x => x.Id == id);

        var access = await projectAccess.GetAsync();
        if (item is null || !access.CanViewProject(item.ProjectId))
        {
            Logger.LogWarning("Todo not found or not visible. TodoId={TodoId}", id);
            return new TodoAdapterModel();
        }

        access.HideInvisibleSourceMeetings([item]);
        return Mapper.Map<TodoAdapterModel>(item);
    }

    #endregion

    #region 新增 / 修改 / 刪除

    /// <param name="via">入口（1.0.34 稽核用）：AI 抽出待辦批次加入時傳 <see cref="AuditVia.AiExtract"/>。</param>
    public async Task<VerifyRecordResult> AddAsync(TodoAdapterModel paraObject, string via = AuditVia.Web)
    {
        Logger.LogInformation("Creating todo. Title={TodoTitle}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        if (!(await projectAccess.GetAsync()).CanViewProject(paraObject.ProjectId))
        {
            if (await context.Project.AnyAsync(x => x.Id == paraObject.ProjectId))
            {
                await AuditDataScopeDeniedAsync(nameof(Project), paraObject.ProjectId, "TodoCreate");
            }

            return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
        }

        // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
        if (await ProjectArchiveRules.CheckWritableAsync(context, paraObject.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
        {
            return VerifyRecordResultFactory.Build(false, archivedError);
        }

        // 子項目（1.0.45）：只分一層、與主待辦同專案。
        var hierarchyError = await TodoHierarchyRules.ValidateAsync(context, null, paraObject.ProjectId, paraObject.ParentTodoId);
        if (hierarchyError is not null)
        {
            return VerifyRecordResultFactory.Build(false, hierarchyError);
        }

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo itemParameter = Mapper.Map<Todo>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Todo.AddAsync(itemParameter);

            // 掛到主待辦底下：主待辦的異動紀錄記「新增子項目」（不通知），與新增同一次存檔。
            if (itemParameter.ParentTodoId is { } parentId)
            {
                var sourceTitle = via == AuditVia.AiExtract && itemParameter.MeetingId is { } meetingId
                    ? await context.Meeting.AsNoTracking().Where(x => x.Id == meetingId).Select(x => x.Title).FirstOrDefaultAsync()
                    : null;
                await changeRecorder.StageSubtaskAsync(
                    parentId, itemParameter.ProjectId, itemParameter.Title, added: true, via,
                    via == AuditVia.AiExtract ? itemParameter.MeetingId : null, sourceTitle);
            }

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);
            changeRecorder.Publish([]);

            await AuditAsync(AuditActions.TodoCreate, itemParameter.Id, $"title={itemParameter.Title}; projectId={itemParameter.ProjectId}; via={via}");
            Logger.LogInformation("Todo created successfully. TodoId={TodoId}, Title={TodoTitle}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條連線共用的：失敗留下的追蹤實體會在下一次任何人存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

            // 原本的專案與要搬去的專案都得看得到——否則能把待辦搬進別人的專案，或從別人的專案搬走。
            var access = await projectAccess.GetAsync();
            if (!access.CanViewProject(item.ProjectId) || !access.CanViewProject(paraObject.ProjectId))
            {
                Logger.LogWarning("Todo update denied by project access. TodoId={TodoId}", paraObject.Id);
                await AuditDataScopeDeniedAsync(nameof(Todo), paraObject.Id, "Update");
                return VerifyRecordResultFactory.Build(false, "找不到要修改的待辦事項。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限；搬進封存專案也不行。
            if ((await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, access) ?? await ProjectArchiveRules.CheckWritableAsync(context, paraObject.ProjectId, access)) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            // 子項目（1.0.45）：只分一層、與主待辦同專案、不能掛自己、已有子項目的不能再當子項目。
            var hierarchyError = await TodoHierarchyRules.ValidateAsync(context, item.Id, paraObject.ProjectId, paraObject.ParentTodoId);
            if (hierarchyError is not null)
            {
                return VerifyRecordResultFactory.Build(false, hierarchyError);
            }

            Todo itemData = Mapper.Map<Todo>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;
            // 來源會議紀錄不開放從畫面修改，一律沿用既有值；但搬到別的專案時清掉（0.4.115）——
            // 來源會議屬於原專案，留著的話新專案的人會看到他們看不到的會議標題。
            itemData.MeetingId = item.ProjectId == paraObject.ProjectId ? item.MeetingId : null;

            CleanTrackingHelper.Clean<Todo>(context);
            context.Entry(itemData).State = EntityState.Modified;

            // 1.0.44：異動紀錄與通知和待辦同一次存檔；item 是存檔前的舊值。
            var recipients = await changeRecorder.StageAsync(item, itemData, AuditVia.Web);

            // 1.0.45：換了主待辦，新舊主待辦的異動紀錄各記一筆。
            if (item.ParentTodoId != itemData.ParentTodoId)
            {
                if (item.ParentTodoId is { } oldParent)
                {
                    await changeRecorder.StageSubtaskAsync(oldParent, item.ProjectId, itemData.Title, added: false, AuditVia.Web);
                }

                if (itemData.ParentTodoId is { } newParent)
                {
                    await changeRecorder.StageSubtaskAsync(newParent, itemData.ProjectId, itemData.Title, added: true, AuditVia.Web);
                }
            }

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);
            changeRecorder.Publish(recipients);

            // 主待辦搬到別的專案時，子項目跟著搬（子項目必須和主待辦同專案）。
            if (item.ProjectId != itemData.ProjectId)
            {
                await TodoHierarchyRules.MoveChildrenAsync(context, itemData.Id, itemData.ProjectId);
            }

            var moved = item.ProjectId != itemData.ProjectId ? $"; movedFrom={item.ProjectId}" : string.Empty;
            await AuditAsync(AuditActions.TodoUpdate, itemData.Id, $"title={itemData.Title}; projectId={itemData.ProjectId}{moved}; via={AuditVia.Web}");
            Logger.LogInformation("Todo updated successfully. TodoId={TodoId}, Title={TodoTitle}", itemData.Id, itemData.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條連線共用的：失敗留下的追蹤實體會在下一次任何人存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to update todo. TodoId={TodoId}, Title={TodoTitle}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改待辦事項失敗。", ex);
        }
    }

    /// <summary>
    /// 從會議紀錄抽出待辦時「更新既有待辦」（1.0.44）：後來的會議又講到同一件事、期限或負責人改了。
    ///
    /// <para>
    /// <b>只改期限與負責人</b>，而且只有新值有填、與舊值不同才改；標題、描述、優先度、狀態、來源會議都保留——
    /// 那些是既有待辦的身分，AI 這次的措辭不同不代表要改掉。期限沒講（null）時不會把既有期限清掉。
    /// 異動紀錄記下是從哪一場會議抽出的，並通知專案成員。
    /// </para>
    /// </summary>
    public async Task<VerifyRecordResult> ApplyExtractedUpdateAsync(
        int todoId,
        DateTime? dueDate,
        string? owner,
        int meetingId,
        string? meetingTitle)
    {
        Logger.LogInformation("Updating todo from extraction. TodoId={TodoId}, MeetingId={MeetingId}", todoId, meetingId);

        try
        {
            CleanTrackingHelper.Clean<Todo>(context);
            Todo? item = await context.Todo.FirstOrDefaultAsync(x => x.Id == todoId);
            if (item == null || !(await projectAccess.GetAsync()).CanViewProject(item.ProjectId))
            {
                CleanTrackingHelper.Clean<Todo>(context);
                return VerifyRecordResultFactory.Build(false, "找不到要更新的待辦事項，可能已經被刪除。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
            {
                CleanTrackingHelper.Clean<Todo>(context);
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            var before = CloneForChange(item);

            if (dueDate is { } newDue && item.DueDate?.Date != newDue.Date)
            {
                item.DueDate = newDue.Date;
            }

            var newOwner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
            if (newOwner is not null && !string.Equals(item.Owner?.Trim(), newOwner, StringComparison.Ordinal))
            {
                item.Owner = newOwner;
            }

            if (TodoChangeRecorder.Diff(before, item).Count == 0)
            {
                CleanTrackingHelper.Clean<Todo>(context);
                return VerifyRecordResultFactory.Build(true);
            }

            item.UpdatedAt = DateTime.Now;

            var recipients = await changeRecorder.StageAsync(before, item, AuditVia.AiExtract, meetingId, meetingTitle);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);
            changeRecorder.Publish(recipients);

            await AuditAsync(AuditActions.TodoUpdate, item.Id, $"title={item.Title}; projectId={item.ProjectId}; sourceMeetingId={meetingId}; via={AuditVia.AiExtract}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條連線共用的：失敗留下的追蹤實體會在下一次任何人存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to update todo from extraction. TodoId={TodoId}", todoId);
            return VerifyRecordResultFactory.Build(false, "更新既有待辦失敗。", ex);
        }
    }

    /// <summary>異動紀錄（1.0.44），新到舊。看不到的待辦回空清單。</summary>
    public async Task<List<TodoChange>> GetChangesAsync(int todoId)
    {
        var projectId = await (await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking())
            .Where(x => x.Id == todoId)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync();

        if (projectId is null)
        {
            return [];
        }

        return await context.TodoChange
            .AsNoTracking()
            .Where(x => x.TodoId == todoId)
            .OrderByDescending(x => x.ChangedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync();
    }

    /// <summary>「期限已變更」標示的期間：最近這幾天內改過期限才標。</summary>
    public const int DueDateChangedBadgeDays = 7;

    /// <summary>
    /// 幫清單上的待辦補上「最近 7 天內改過期限」的資訊（1.0.44）：一次查詢涵蓋整頁，不逐筆查。
    /// </summary>
    private async Task FillRecentDueDateChangesAsync(IReadOnlyCollection<TodoAdapterModel> todos)
    {
        if (todos.Count == 0)
        {
            return;
        }

        var ids = todos.Select(x => x.Id).ToList();
        var since = DateTime.Now.AddDays(-DueDateChangedBadgeDays);
        var changes = await context.TodoChange
            .AsNoTracking()
            .Where(x => ids.Contains(x.TodoId) && x.Field == TodoChangeFields.DueDate && x.ChangedAt >= since)
            .OrderByDescending(x => x.ChangedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync();

        var latest = changes.GroupBy(x => x.TodoId).ToDictionary(g => g.Key, g => g.First());
        foreach (var todo in todos)
        {
            if (latest.TryGetValue(todo.Id, out var change))
            {
                todo.DueDateChangedAt = change.ChangedAt;
                todo.DueDateChangeText =
                    $"原 {TodoChangeRecorder.DisplayValue(TodoChangeFields.DueDate, change.OldValue)}，"
                    + $"{change.ChangedByName ?? "有人"} 於 {change.ChangedAt:MM/dd HH:mm} 改為 {TodoChangeRecorder.DisplayValue(TodoChangeFields.DueDate, change.NewValue)}"
                    + $"（{TodoChangeFields.DescribeVia(change.Via, change.SourceMeetingTitle)}）";
            }
        }
    }

    /// <summary>
    /// 幫這一頁的主待辦帶出子項目（1.0.45）。子項目照篩選結果顯示（<paramref name="matching"/>）；
    /// 「子項目 1/3」的總數與完成數則看全部子項目，不受篩選影響——否則篩「進行中」時會顯示 0/0 這種誤導的數字。
    /// </summary>
    private async Task FillChildrenAsync(List<TodoAdapterModel> parents, IQueryable<Todo> matching, ProjectAccess access)
    {
        if (parents.Count == 0)
        {
            return;
        }

        var ids = parents.Select(x => x.Id).ToList();
        var children = await matching
            .Where(x => x.ParentTodoId != null && ids.Contains(x.ParentTodoId.Value))
            .OrderBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenBy(x => x.Id)
            .ToListAsync();
        access.HideInvisibleSourceMeetings(children);

        var counts = await context.Todo.AsNoTracking()
            .Where(x => x.ParentTodoId != null && ids.Contains(x.ParentTodoId.Value))
            .GroupBy(x => x.ParentTodoId!.Value)
            .Select(g => new { ParentId = g.Key, Total = g.Count(), Completed = g.Count(x => x.Status == TodoAdapterModel.CompletedStatus) })
            .ToDictionaryAsync(x => x.ParentId);

        var childModels = Mapper.Map<List<TodoAdapterModel>>(children).ToLookup(x => x.ParentTodoId!.Value);
        foreach (var parent in parents)
        {
            var mine = childModels[parent.Id].ToList();
            foreach (var child in mine)
            {
                child.ParentTitle = parent.Title;
            }

            // 沒有子項目時給 null：AntDesign 的 TreeChildren 看到空集合也會畫出展開鈕。
            parent.Children = mine.Count > 0 ? mine : null;
            if (counts.TryGetValue(parent.Id, out var count))
            {
                parent.ChildrenTotal = count.Total;
                parent.ChildrenCompleted = count.Completed;
            }
        }
    }

    /// <summary>可選的主待辦（1.0.45）：同專案、本身不是子項目、未完成、不是自己。編輯視窗的「屬於」下拉用。</summary>
    public async Task<List<TodoAdapterModel>> GetParentCandidatesAsync(int projectId, int? excludeId = null)
    {
        var access = await projectAccess.GetAsync();
        if (!access.CanViewProject(projectId))
        {
            return [];
        }

        var items = await context.Todo.AsNoTracking()
            .Where(x => x.ProjectId == projectId
                && x.ParentTodoId == null
                && x.Status != TodoAdapterModel.CompletedStatus
                && (excludeId == null || x.Id != excludeId))
            .OrderBy(x => x.Title)
            .ToListAsync();

        return Mapper.Map<List<TodoAdapterModel>>(items);
    }

    /// <summary>異動比對用的快照：只複製會被比對的欄位。</summary>
    private static Todo CloneForChange(Todo item) => new()
    {
        Id = item.Id,
        ProjectId = item.ProjectId,
        Title = item.Title,
        DueDate = item.DueDate,
        Owner = item.Owner,
        Status = item.Status,
        Priority = item.Priority,
    };

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

            if (!(await projectAccess.GetAsync()).CanViewProject(item.ProjectId))
            {
                // 對使用者仍當成不存在；稽核記下有人想改看不到的待辦（1.0.34）。
                CleanTrackingHelper.Clean<Todo>(context);
                await AuditDataScopeDeniedAsync(nameof(Todo), id, isCompleted ? "Complete" : "Reopen");
                return VerifyRecordResultFactory.Build(false, "找不到要更新的待辦事項。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
            {
                CleanTrackingHelper.Clean<Todo>(context);
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            // 1.0.44：改之前先留一份舊值給異動紀錄（item 是追蹤中的實體，下一行就會被改掉）。
            var before = CloneForChange(item);

            // 取消完成時退回「進行中」而非「待辦」——已經動過的事情退回未開始並不合理。
            item.Status = isCompleted
                ? TodoAdapterModel.CompletedStatus
                : TodoAdapterModel.StatusOptions[1];
            item.UpdatedAt = DateTime.Now;

            var recipients = await changeRecorder.StageAsync(before, item, AuditVia.Web);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);
            changeRecorder.Publish(recipients);

            await AuditAsync(isCompleted ? AuditActions.TodoComplete : AuditActions.TodoReopen, id, $"title={item.Title}; status={item.Status}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條連線共用的：失敗留下的追蹤實體會在下一次任何人存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

            if (item == null || !(await projectAccess.GetAsync()).CanViewProject(item.ProjectId))
            {
                Logger.LogWarning("Todo deletion rejected because record was not found or not visible. TodoId={TodoId}", id);
                if (item is not null)
                {
                    await AuditDataScopeDeniedAsync(nameof(Todo), id, "Delete");
                }

                return VerifyRecordResultFactory.Build(false, "找不到要刪除的待辦事項。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            // 子項目（1.0.45）：刪主待辦時資料庫 Cascade 一起刪掉子項目；刪子項目時在主待辦記「移除子項目」。
            var childCount = await context.Todo.AsNoTracking().CountAsync(x => x.ParentTodoId == id);

            CleanTrackingHelper.Clean<Todo>(context);
            context.Entry(item).State = EntityState.Deleted;
            if (item.ParentTodoId is { } parentId)
            {
                await changeRecorder.StageSubtaskAsync(parentId, item.ProjectId, item.Title, added: false, AuditVia.Web);
            }

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Todo>(context);
            changeRecorder.Publish([]);

            var children = childCount > 0 ? $"; children={childCount}" : string.Empty;
            await AuditAsync(AuditActions.TodoDelete, id, $"title={item.Title}; projectId={item.ProjectId}{children}; via={AuditVia.Web}");
            Logger.LogInformation("Todo deleted successfully. TodoId={TodoId}, Title={TodoTitle}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條連線共用的：失敗留下的追蹤實體會在下一次任何人存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

        if (project is null || !(await projectAccess.GetAsync()).CanViewProject(project.Id))
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
        CancellationToken cancellationToken = default,
        ProjectScope scope = ProjectScope.All)
    {
        // 1.0.46：跟著「使用中｜已完成｜全部」檢視走，面板數字才和清單對得上。
        IQueryable<Todo> query = ProjectArchiveRules.ApplyScope((await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking()), scope);
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
            .GroupBy(x => OwnerKey(x.Owner))
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
        CancellationToken cancellationToken = default,
        ProjectScope scope = ProjectScope.All)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return [];
        }

        IQueryable<Todo> query = ProjectArchiveRules.ApplyScope((await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking()).Include(x => x.Project).Include(x => x.Parent), scope);
        if (projectFilter is > 0)
        {
            query = query.Where(x => x.ProjectId == projectFilter);
        }

        var records = await query
            .OrderBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // 在記憶體比對，與 GetOwnerSummariesAsync 的分組鍵用同一個函式（0.4.115）。
        // 以前在 SQL 裡用 trim()，SQLite 只去掉半形空白：負責人尾巴帶全形空白或 Tab 時，
        // 面板算 5 筆、點下去只出現 4 筆。
        records = records.Where(x => OwnerKey(x.Owner) == owner).ToList();

        var models = Mapper.Map<List<TodoAdapterModel>>(records);
        await FillRecentDueDateChangesAsync(models);
        return models;
    }

    private static string OwnerKey(string? owner)
    {
        return string.IsNullOrWhiteSpace(owner) ? UnassignedOwner : owner.Trim();
    }

    #endregion

    // ───────── 稽核（1.0.34）：操作者由 WriteForCurrentUserAsync 決定 ─────────

    private Task AuditAsync(string action, int todoId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: nameof(Todo), targetId: todoId.ToString(), detail: detail);

    /// <summary>資料範圍擋下的寫入（看不到的待辦，或指定了看不到的專案）。</summary>
    private Task AuditDataScopeDeniedAsync(string targetType, int targetId, string operation)
        => auditLogService.WriteForCurrentUserAsync(
            AuditActions.PermissionDenied, success: false, targetType: targetType, targetId: targetId.ToString(),
            detail: $"reason=DataScope; op={operation}");
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
