using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Components.Views.Todos;

public partial class TodoViewView : IAsyncDisposable
{
    private readonly ILogger<TodoViewView> logger;
    private readonly TodoService todoService;
    private readonly ProjectService projectService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private ITable? table;

    private List<ProjectAdapterModel> availableProjects = [];

    private int selectedProjectFilter;
    private string selectedStatusFilter = string.Empty;

    /// <summary>量測用的根元素。<c>todo-auto-fit.js</c> 從這裡往下找表格與面板。</summary>
    private ElementReference rootElement;

    /// <summary>給 JS 回呼用的參照。null 代表還沒掛上觀察者（沒權限時就不會掛）。</summary>
    private DotNetObjectReference<TodoViewView>? autoFitReference;

    /// <summary>
    /// 每頁筆數是否還跟著畫面高度走（0.4.86）。
    ///
    /// <para>
    /// ⚠️ 使用者自己在分頁器上改過每頁筆數之後就關掉，否則他選了 20 筆，
    /// 下一次量測會立刻把它蓋回 11 筆——看起來就像那個選單壞了。
    /// 分頁器的筆數選單在總筆數超過 50 時才會出現（AntDesign 的
    /// <c>TotalBoundaryShowSizeChanger</c> 預設值），所以這條路徑不常走，但走得到。
    /// </para>
    /// </summary>
    private bool autoFitEnabled = true;

    /// <summary>
    /// 自動調整列數的控制器狀態。見 <see cref="TodoFitState"/>——
    /// 沒有那個上限，列數會在「補一列」與「縮一列」之間來回跳，每跳一次還打一輪查詢。
    /// </summary>
    private TodoFitState fitState = TodoAutoFit.InitialState;

    private int _pageIndex = 1;

    /// <summary>
    /// 每頁筆數。0.4.85 是寫死的 5，0.4.86 起改成量畫面高度算出來的
    /// （見 <see cref="TodoAutoFit"/>），這裡的值只是「量到之前」的起手式。
    /// </summary>
    private int _pageSize = TodoAutoFit.FallbackRows;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<TodoAdapterModel> todoAdapterModels = [];

    private string modalTitle = "待辦事項維護";
    private bool modalVisible;
    private TodoAdapterModel CurrentRecord = new();

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>
    /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard="true"</c> 與表單上的
    /// <c>@onkeydown</c>，兩者都會呼叫 <see cref="OnModalCancelHandleAsync"/>。0.4.84 之前兩次
    /// 都只是 <c>modalVisible = false</c>，冪等所以沒人發現；加了確認框之後會**疊出兩個確認視窗**。
    /// </summary>
    private bool isDiscardConfirming;
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IReadOnlyList<TodoOwnerSummary> ownerSummaries = [];
    private IReadOnlyList<TodoAdapterModel> ownerTodos = [];
    private string selectedOwner = string.Empty;

    /// <summary>
    /// 面板小清單的狀態篩選（0.4.85）。點膠囊切換，只影響右邊那份小清單，左邊的表格不動。
    /// </summary>
    private OwnerTodoFilter ownerTodoFilter = OwnerTodoFilter.All;
    private bool ownerPanelCollapsed;

    private string detailTitle = string.Empty;
    private string detailContent = string.Empty;
    private bool detailVisible;

    private TodoOwnerSummary? SelectedOwnerSummary
        => ownerSummaries.FirstOrDefault(x => x.Owner == selectedOwner);

    /// <summary>
    /// 套上膠囊篩選之後的小清單。
    ///
    /// <para>
    /// ⚠️ 刻意是 computed property，不另存一份「篩過的清單」欄位：
    /// <c>ownerTodos</c> 是資料來源、<c>ownerTodoFilter</c> 是檢視狀態，合併之後每一條重載路徑
    /// （面板重載、換人、勾完成、刪除、Modal 送出）都要記得重套一次，
    /// 漏掉任何一條就會顯示上一次的篩選結果，而且畫面看起來完全合理。
    /// </para>
    /// </summary>
    private IReadOnlyList<TodoAdapterModel> FilteredOwnerTodos
        => TodoOwnerFilter.Apply(ownerTodos, ownerTodoFilter);

    /// <summary>面板標題旁的範圍說明。面板只跟專案過濾連動，所以要讓使用者看得出目前算的是哪個範圍。</summary>
    private string OwnerPanelScopeText
        => selectedProjectFilter > 0
            ? availableProjects.FirstOrDefault(x => x.Id == selectedProjectFilter)?.Title ?? "全部專案"
            : "全部專案";

    private static IReadOnlyList<string> StatusOptions => TodoAdapterModel.StatusOptions;
    private static IReadOnlyList<string> PriorityOptions => TodoAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public TodoAutoFitInterop AutoFitInterop { get; set; } = default!;

    public TodoViewView(
        ILogger<TodoViewView> logger,
        TodoService todoService,
        ProjectService projectService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.todoService = todoService;
        this.projectService = projectService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing todo management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Todo view initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_待辦事項) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/todos");
            logger.LogWarning("Todo view denied because current user has not this role permission.");
            return;
        }

        availableProjects = await projectService.GetSelectableAsync(includeArchived: true);

        await ReloadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 沒權限時根本沒算繪那個 <div>，rootElement 還是 default，掛上去只會在 JS 端找不到元素。
        if (!firstRender || !string.IsNullOrEmpty(RoleMessage))
        {
            return;
        }

        autoFitReference = DotNetObjectReference.Create(this);
        await AutoFitInterop.ObserveAsync(rootElement, autoFitReference);
    }

    /// <summary>
    /// JS 量完畫面之後回報（0.4.86）。第一次是掛上觀察者時立刻量一次，
    /// 之後是視窗大小／版面寬度變了，或是我們自己改完列數主動再量一次。
    ///
    /// <para>
    /// ⚠️ 這是**回授**不是一次算到位：每調整一次就再量一次，直到剩餘空間連一列都放不下。
    /// 收斂的理由見 <see cref="TodoAutoFit.Decide"/>。因此載入時會多打一兩次查詢——
    /// 要省掉就得先算繪一個沒有資料的空表格來量，為了幾次查詢把載入流程整個翻掉不划算。
    /// </para>
    /// </summary>
    [JSInvokable]
    public async Task OnAutoFitMeasuredAsync(
        double leftover,
        double tallestRow,
        double shortestRow,
        double contentWidth,
        double viewportHeight)
    {
        if (!autoFitEnabled)
        {
            return;
        }

        var sample = new TodoFitSample(leftover, tallestRow, shortestRow, contentWidth, viewportHeight);
        var decision = TodoAutoFit.Decide(_pageSize, sample, fitState);
        fitState = decision.State;

        if (decision.PageSize == _pageSize)
        {
            return;
        }

        logger.LogDebug(
            "Todo page size auto-fitted. Leftover={Leftover}, TallestRow={TallestRow}, ShortestRow={ShortestRow}, Ceiling={Ceiling}, PageSize={PageSize}",
            leftover,
            tallestRow,
            shortestRow,
            decision.State.Ceiling,
            decision.PageSize);

        // JS 回呼不保證落在算繪執行緒上，改狀態與重載一律包進 InvokeAsync。
        await InvokeAsync(async () =>
        {
            _pageSize = decision.PageSize;

            // 頁碼刻意不歸 1：使用者只是拉了視窗大小，不該被丟回第一頁。
            // 新的每頁筆數讓目前頁碼超出範圍時，ReloadAsync 尾端的夾頁碼會處理。
            await ReloadAsync();

            // ⚠️ 一定要再量一次。ResizeObserver 只看寬度，而我們剛剛只改了高度——
            //    少了這一行，列數就只會被調整一次，永遠停在第一次估的值。
            await AutoFitInterop.RemeasureAsync(rootElement);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (autoFitReference is null)
        {
            return;
        }

        await AutoFitInterop.DisconnectAsync(rootElement);
        autoFitReference.Dispose();
        autoFitReference = null;
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading todos. Search={Search}, ProjectFilter={ProjectFilter}, StatusFilter={StatusFilter}, SortField={SortField}, PageIndex={PageIndex}",
            searchText,
            selectedProjectFilter,
            selectedStatusFilter,
            sortField,
            _pageIndex);

        DataRequestResult<TodoAdapterModel> dataRequestResult = await todoService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
            ProjectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : null,
            ProjectScope = projectScope,
            StatusFilter = string.IsNullOrWhiteSpace(selectedStatusFilter) ? null : selectedStatusFilter,
        });

        todoAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;

        // 分頁修好之後（0.4.85）頁碼有可能落在最後一頁之後——例如停在第 2 頁時把該頁
        // 唯一一筆刪掉，Skip 就會跳過全部資料而顯示空白表格。夾回最後一頁重載一次。
        //
        // ⚠️ Math.Max(1, ...) 不可省：AntDesign 在 PageIndex < 1 時不會觸發 OnChange，
        //    夾出 0 會讓分頁器再也叫不動。
        // 遞迴有界：Count 與資料列是同一個查詢算出來的，_total > 0 代表夾到的最後一頁必定非空。
        if (todoAdapterModels.Count == 0 && _total > 0 && _pageIndex > 1)
        {
            _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            logger.LogDebug("Todo page index clamped to the last page. PageIndex={PageIndex}", _pageIndex);
            await ReloadAsync();
            return;
        }

        // 面板與清單一律同進同出。所有變更資料的路徑（勾完成、刪除、Modal 送出）都只呼叫
        // ReloadAsync，掛在這裡才不會出現「左邊變了、右邊完成度不動」。
        await ReloadOwnerPanelAsync();

        logger.LogInformation("Todo list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    #region 過濾與排序

    #region 已完成＝封存（1.0.46）

    /// <summary>「使用中｜已完成｜全部」，預設使用中：已完成專案的待辦預設不列出。每次開頁都回到使用中。</summary>
    private ProjectScope projectScope = ProjectScope.Active;

    /// <summary>專案篩選下拉：跟著檢視列出對應的專案。</summary>
    private List<ProjectAdapterModel> ScopedProjects
        => availableProjects.Where(p => ProjectArchiveRules.Matches(p.Status, projectScope)).ToList();

    /// <summary>新增／編輯表單的專案選單：不列封存專案（正在編輯的那筆原本就在封存專案時例外，避免顯示空白）。</summary>
    private List<ProjectAdapterModel> FormProjects
        => availableProjects.Where(p => !p.IsArchived || p.Id == CurrentRecord.ProjectId).ToList();

    /// <summary>封存專案的待辦唯讀；管理者不受限（全站慣例）。</summary>
    private bool IsReadOnly(TodoAdapterModel todo) => todo.IsProjectArchived && !AuthenticationStateHelper.CheckIsAdmin();

    private async Task OnProjectScopeChangedAsync(ProjectScope scope)
    {
        projectScope = scope;

        // 篩選中的專案不在新的檢視範圍裡，就清掉篩選，不然清單會是空的而且看不出為什麼。
        if (selectedProjectFilter > 0 && ScopedProjects.All(p => p.Id != selectedProjectFilter))
        {
            selectedProjectFilter = 0;
        }

        _pageIndex = 1;
        await ReloadAsync();
    }

    #endregion

    private async Task OnProjectFilterChanged(int value)
    {
        selectedProjectFilter = value;
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnStatusFilterChanged(string value)
    {
        selectedStatusFilter = value ?? string.Empty;
        _pageIndex = 1;
        await ReloadAsync();
    }

    /// <summary>
    /// 重載右側負責人面板。刻意只帶專案過濾——不帶狀態與關鍵字，理由見 GetOwnerSummariesAsync 的註解。
    /// </summary>
    private async Task ReloadOwnerPanelAsync()
    {
        var projectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : (int?)null;
        ownerSummaries = await todoService.GetOwnerSummariesAsync(projectFilter, scope: projectScope);

        // 換專案後原本選中的人可能在新範圍裡沒有任何待辦，退回第一位而不是留一個空面板。
        if (ownerSummaries.All(x => x.Owner != selectedOwner))
        {
            selectedOwner = ownerSummaries.FirstOrDefault()?.Owner ?? string.Empty;
        }

        ownerTodos = string.IsNullOrEmpty(selectedOwner)
            ? []
            : await todoService.GetByOwnerAsync(selectedOwner, projectFilter, scope: projectScope);

        ClampOwnerTodoFilter();
    }

    /// <summary>
    /// 四顆膠囊的資料來源。逾期那顆維持「0 筆就不渲染」的既有行為（0.4.66）。
    /// </summary>
    private static IEnumerable<(OwnerTodoFilter Filter, string Label, int Count, string CssClass)> OwnerStatPills(
        TodoOwnerSummary summary)
    {
        yield return (OwnerTodoFilter.Pending, TodoOwnerFilter.Describe(OwnerTodoFilter.Pending), summary.Pending, "todo-view-status-pending");
        yield return (OwnerTodoFilter.InProgress, TodoOwnerFilter.Describe(OwnerTodoFilter.InProgress), summary.InProgress, "todo-view-status-processing");
        yield return (OwnerTodoFilter.Completed, TodoOwnerFilter.Describe(OwnerTodoFilter.Completed), summary.Completed, "todo-view-status-completed");

        if (summary.Overdue > 0)
        {
            yield return (OwnerTodoFilter.Overdue, TodoOwnerFilter.Describe(OwnerTodoFilter.Overdue), summary.Overdue, "todo-view-overdue");
        }
    }

    /// <summary>
    /// 點膠囊＝篩「這個人 ＋ 這個狀態」，再點同一顆回到全部。
    ///
    /// <para>
    /// 刻意不重撈資料：<c>ownerTodos</c> 已經是這個人的全部，篩選純粹是檢視狀態。
    /// 也刻意是**互斥單選**——逾期 ∩ 已完成 恆為空，允許複選一定會做出「永遠是空清單」的組合；
    /// 而且單選才保得住「膠囊上的數字 = 點下去看到的筆數」。
    /// </para>
    /// </summary>
    private void ToggleOwnerTodoFilter(OwnerTodoFilter filter)
    {
        ownerTodoFilter = ownerTodoFilter == filter ? OwnerTodoFilter.All : filter;
        logger.LogDebug("Todo owner panel filter changed. Owner={Owner}, Filter={Filter}", selectedOwner, ownerTodoFilter);
    }

    private void ClearOwnerTodoFilter() => ownerTodoFilter = OwnerTodoFilter.All;

    /// <summary>
    /// 收合／展開右側面板。收合時外層 grid 退回單欄，表格拿回整個寬度。
    /// 刻意不記在 localStorage——這是檢視偏好，重新整理回到展開是可預期的。
    /// </summary>
    private void ToggleOwnerPanel()
    {
        ownerPanelCollapsed = !ownerPanelCollapsed;
        logger.LogDebug("Todo owner panel toggled. Collapsed={Collapsed}", ownerPanelCollapsed);
    }

    private async Task OnOwnerChangedAsync(string owner)
    {
        selectedOwner = owner ?? string.Empty;
        logger.LogDebug("Todo owner panel switched. Owner={Owner}", selectedOwner);

        var projectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : (int?)null;
        ownerTodos = string.IsNullOrEmpty(selectedOwner)
            ? []
            : await todoService.GetByOwnerAsync(selectedOwner, projectFilter, scope: projectScope);

        // 換人刻意**不重設**篩選：典型用法是跨人巡同一個狀態（「還有誰有逾期」），
        // 每換一個人就要重點一次等於把功能做廢一半。對新的人沒意義的篩選由 clamp 收掉。
        ClampOwnerTodoFilter();
    }

    /// <summary>
    /// 把作用中的篩選收斂回合法狀態：選中的膠囊變 0 筆時回到「全部」。
    ///
    /// <para>
    /// 守的不變式是「作用中的篩選，畫面上一定要有一顆可以點掉它的膠囊」。典型情境是
    /// 篩了逾期、再把最後一筆逾期的改成已完成——逾期膠囊會整個消失，篩選若留著就變成
    /// 「空清單，而且沒有任何東西可以按掉它」。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 日後若有人想改成「換專案要重設篩選」，那一行只能放在
    /// <see cref="OnProjectFilterChanged"/>，<b>絕不能放進 <c>ReloadOwnerPanelAsync</c></b>——
    /// 後者在每次勾完成／刪除／新增之後都會跑，放那裡等於使用者每勾一個完成，篩選就被默默清掉。
    /// </para>
    /// </summary>
    private void ClampOwnerTodoFilter()
        => ownerTodoFilter = TodoOwnerFilter.Clamp(SelectedOwnerSummary, ownerTodoFilter);

    /// <summary>開啟待辦內容的唯讀檢視。清單只顯示大綱，完整描述在這裡看。</summary>
    private void OpenDetail(TodoAdapterModel todoAdapterModel)
    {
        detailTitle = todoAdapterModel.Title;
        detailContent = todoAdapterModel.Description ?? string.Empty;
        detailVisible = true;
        logger.LogDebug("Opened todo detail view. TodoId={TodoId}", todoAdapterModel.Id);
    }

    private void OnDetailCancel(MouseEventArgs args)
    {
        detailVisible = false;
    }

    #region 異動紀錄與深連結（1.0.44）

    /// <summary>
    /// 從鈴鐺通知點進來時帶的待辦（<c>/todos?todoId=N</c>）：切到它的專案並打開它的異動紀錄。
    /// 待辦頁已經開著時再點另一則通知，元件不會重建、只會重新設參數，所以在 OnParametersSet 處理。
    /// </summary>
    [Parameter]
    public int? TodoId { get; set; }

    private int? handledTodoId;

    private bool changesVisible;
    private bool changesLoading;
    private string changesTitle = "異動紀錄";
    private List<MeetingRecord.AccessDatas.Models.TodoChange> changes = [];

    protected override async Task OnParametersSetAsync()
    {
        if (TodoId is not { } id || id <= 0 || id == handledTodoId || !string.IsNullOrEmpty(RoleMessage))
        {
            return;
        }

        handledTodoId = id;
        var todo = await todoService.GetAsync(id);
        if (todo.Id == 0)
        {
            _ = messageService.WarningAsync("找不到這筆待辦，可能已經被刪除或你沒有權限看。");
            return;
        }

        // 切到它所屬的專案，清單上才找得到它；異動紀錄直接打開，通知點進來最想看的就是「改了什麼」。
        // 1.0.46：封存專案的待辦在「使用中」看不到，切到全部。
        var scopeMismatch = !ProjectArchiveRules.Matches(todo.ProjectStatus, projectScope);
        if (selectedProjectFilter != todo.ProjectId || scopeMismatch)
        {
            selectedProjectFilter = todo.ProjectId;
            if (scopeMismatch)
            {
                projectScope = ProjectScope.All;
            }

            _pageIndex = 1;
            await ReloadAsync();
        }

        await OpenChangesAsync(todo.Id, todo.Title);
    }

    private async Task OpenChangesAsync(int todoId, string? title)
    {
        changesTitle = $"異動紀錄 — {title}";
        changes = [];
        changesLoading = true;
        changesVisible = true;
        StateHasChanged();

        try
        {
            changes = await todoService.GetChangesAsync(todoId);
        }
        finally
        {
            changesLoading = false;
        }
    }

    private void OnChangesCancel(MouseEventArgs args)
    {
        changesVisible = false;
    }

    #endregion

    private async Task OnTableChange(QueryModel<TodoAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        // 0.4.85 之前分頁根本沒作用，所以「使用者改每頁筆數」不可能被觀察到；修好之後它第一次真的會動。
        // 只靠 @bind-PageSize 的話，要賭 AntDesign 內部 callback 與繫結回寫的先後順序——
        // 那是沒有文件保證的實作細節，明確賦值把它釘死（與 AiUsageView 一致）。
        //
        // _pageSize 一定是我們自己最後設進去的值（回退值或量測值），所以回報的筆數只要跟它不同，
        // 就只可能來自分頁器上的筆數選單——也就是使用者自己選的。從這一刻起不再自動調整，
        // 否則他選了 20 筆，下一次量測會立刻蓋回去（0.4.86）。
        if (autoFitEnabled && args.PageSize != _pageSize)
        {
            autoFitEnabled = false;
            logger.LogDebug("Todo page size auto-fit disabled because the user picked {PageSize} manually.", args.PageSize);
        }

        _pageSize = args.PageSize;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = GetCurrentSortModel(args.SortModel);
            sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
            sortField = ResolveSortFieldName(tableSortModel);
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        logger.LogDebug("Todo table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }

    private static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
    {
        return sortModels.FirstOrDefault(model => HasSortDirection(model.SortDirection))
            ?? sortModels.Last();
    }

    private static bool HasSortDirection(SortDirection sortDirection)
    {
        return sortDirection == SortDirection.Ascending || sortDirection == SortDirection.Descending;
    }

    private static string ResolveSortFieldName(ITableSortModel sortModel)
    {
        if (!string.IsNullOrWhiteSpace(sortModel.FieldName))
        {
            return sortModel.FieldName;
        }

        object? column = sortModel.GetType().GetProperty("Column")?.GetValue(sortModel);
        if (column is null)
        {
            return string.Empty;
        }

        string? columnFieldName = column.GetType().GetProperty("FieldName")?.GetValue(column)?.ToString();
        if (!string.IsNullOrWhiteSpace(columnFieldName))
        {
            return columnFieldName;
        }

        object? dataIndex = column.GetType().GetProperty("DataIndex")?.GetValue(column);
        return dataIndex?.ToString() ?? string.Empty;
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        logger.LogInformation("Todo search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Todo refresh triggered.");
        availableProjects = await projectService.GetSelectableAsync(includeArchived: true);
        await ReloadAsync();
        NotifySuccess("已更新最新資料");
    }

    #endregion

    #region 新增 / 修改 / 刪除 / 完成

    /// <summary>清單上的勾選框。完成與否只寫 Status 一個欄位。</summary>
    private async Task OnCompletedChangedAsync(TodoAdapterModel todo, bool isCompleted)
    {
        // 勾選框沒權限時只是停用，處理器仍掛著；這裡再擋一次（0.4.115）。
        if (!AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit))
        {
            return;
        }

        var result = await todoService.SetCompletedAsync(todo.Id, isCompleted);
        if (!result.Success)
        {
            NotifyError(result.Message);
        }

        await ReloadAsync();
    }

    private async Task OnAddAsync()
    {
        // 開表單前重載可選專案（0.4.115）：有人替你加了協作團隊後，清單已經看得到新專案的待辦，
        // 這裡沒更新的話，編輯那筆時「所屬專案」會是空白。
        availableProjects = await projectService.GetSelectableAsync(includeArchived: true);
        CurrentRecord = new TodoAdapterModel
        {
            Status = StatusOptions[0],
            Priority = PriorityOptions[1],
            // 已經套用專案過濾時，新增的待辦預設落在同一個專案，省一次選取；
            // 但不會落在已完成（封存）的專案（1.0.46）。
            ProjectId = availableProjects.Any(p => p.Id == selectedProjectFilter && !p.IsArchived)
                ? selectedProjectFilter
                : availableProjects.FirstOrDefault(p => !p.IsArchived)?.Id ?? 0,
        };

        isNewRecordMode = true;
        modalTitle = "新增待辦事項";
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        logger.LogInformation("Opened create modal for todo.");
    }

    #region 子項目（1.0.45）

    /// <summary>編輯視窗「屬於（主待辦）」的選項：同專案、本身不是子項目、未完成、不是自己。</summary>
    private List<TodoAdapterModel> parentCandidates = [];

    private async Task LoadParentCandidatesAsync()
    {
        parentCandidates = CurrentRecord.ProjectId > 0
            ? await todoService.GetParentCandidatesAsync(CurrentRecord.ProjectId, CurrentRecord.Id > 0 ? CurrentRecord.Id : null)
            : [];
    }

    /// <summary>換專案時主待辦一定不適用了（子項目必須與主待辦同專案），清掉並重載選項。</summary>
    private async Task OnFormProjectChangedAsync()
    {
        if (CurrentRecord.ParentTodoId is not null && parentCandidates.All(x => x.ProjectId != CurrentRecord.ProjectId))
        {
            CurrentRecord.ParentTodoId = null;
        }

        await LoadParentCandidatesAsync();
    }

    /// <summary>主待辦列的「新增子項目」：預填同專案與主待辦，其他欄位照一般新增。</summary>
    private async Task OnAddSubtaskAsync(TodoAdapterModel parent)
    {
        availableProjects = await projectService.GetSelectableAsync(includeArchived: true);
        CurrentRecord = new TodoAdapterModel
        {
            Status = StatusOptions[0],
            Priority = PriorityOptions[1],
            ProjectId = parent.ProjectId,
            ParentTodoId = parent.Id,
        };

        isNewRecordMode = true;
        modalTitle = $"新增子項目 — {parent.Title}";
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        logger.LogInformation("Opened create modal for subtask. ParentTodoId={ParentTodoId}", parent.Id);
    }

    #endregion

    private async Task OnEditAsync(TodoAdapterModel todoAdapterModel)
    {
        availableProjects = await projectService.GetSelectableAsync(includeArchived: true);
        isNewRecordMode = false;
        modalTitle = "修改待辦事項";
        CurrentRecord = todoAdapterModel.Clone();
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        logger.LogInformation("Opened edit modal for todo. TodoId={TodoId}", todoAdapterModel.Id);
    }

    private async Task OnDeleteAsync(TodoAdapterModel todoAdapterModel)
    {
        logger.LogInformation("Delete todo requested. TodoId={TodoId}", todoAdapterModel.Id);

        var beforeDeleteCheckResult = await todoService.BeforeDeleteCheckAsync(todoAdapterModel);
        if (!beforeDeleteCheckResult.Success)
        {
            NotifyError(beforeDeleteCheckResult.Message);
            return;
        }

        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            // 1.0.45：主待辦連子項目一起刪，要先講清楚。
            Content = todoAdapterModel.ChildrenTotal > 0
                ? $"確定要刪除這筆待辦事項嗎？它底下的 {todoAdapterModel.ChildrenTotal} 個子項目也會一起刪除。此操作無法復原。"
                : "確定要刪除這筆待辦事項嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Todo delete cancelled by user. TodoId={TodoId}", todoAdapterModel.Id);
            return;
        }

        var result = await todoService.DeleteAsync(todoAdapterModel.Id);
        if (!result.Success)
        {
            NotifyError(result.Message);
            return;
        }

        logger.LogInformation("Todo delete completed. TodoId={TodoId}", todoAdapterModel.Id);
        NotifySuccess("刪除成功");
        await ReloadAsync();
    }

    /// <summary>存檔中（0.4.115）：擋重複送出——連按確定或連按 Enter 會建出兩筆待辦。</summary>
    private bool isSaving;

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (isSaving)
        {
            return;
        }

        isSaving = true;
        try
        {
            await SaveModalAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task SaveModalAsync()
    {
        if (LocalEditContext?.Validate() == false)
        {
            foreach (var error in LocalEditContext.GetValidationMessages())
            {
                logger.LogWarning("Todo form validation failed. Error={Error}", error);
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "驗證失敗",
                    Description = error,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight,
                    Duration = 5
                });
            }

            modalVisible = true;
            return;
        }

        VerifyRecordResult actionResult;

        if (isNewRecordMode)
        {
            var beforeAddCheckResult = await todoService.BeforeAddCheckAsync(CurrentRecord);
            if (!beforeAddCheckResult.Success)
            {
                logger.LogWarning("Todo create pre-check failed. Message={Message}", beforeAddCheckResult.Message);
                NotifyError(beforeAddCheckResult.Message);
                modalVisible = true;
                return;
            }

            CurrentRecord.CreatedAt = DateTime.Now;
            CurrentRecord.UpdatedAt = DateTime.Now;
            actionResult = await todoService.AddAsync(CurrentRecord);
        }
        else
        {
            var beforeUpdateCheckResult = await todoService.BeforeUpdateCheckAsync(CurrentRecord);
            if (!beforeUpdateCheckResult.Success)
            {
                logger.LogWarning("Todo update pre-check failed. TodoId={TodoId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                NotifyError(beforeUpdateCheckResult.Message);
                modalVisible = true;
                return;
            }

            CurrentRecord.UpdatedAt = DateTime.Now;
            actionResult = await todoService.UpdateAsync(CurrentRecord);
        }

        if (!actionResult.Success)
        {
            NotifyError(actionResult.Message);
            modalVisible = true;
            return;
        }

        NotifySuccess(isNewRecordMode ? "新增成功" : "修改成功");
        if (isNewRecordMode)
        {
            _ = messageService.SuccessAsync("新增成功");
        }

        await ReloadAsync();
        modalVisible = false;
    }

    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        if (isDiscardConfirming)
        {
            return;
        }

        if (FormDirtyHelper.IsDirty(formSnapshot, CurrentRecord))
        {
            isDiscardConfirming = true;
            bool discard;
            try
            {
                discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆待辦事項");
            }
            finally
            {
                isDiscardConfirming = false;
            }

            if (!discard)
            {
                // ⚠️ @bind-Visible 是雙向的，AntDesign 在呼叫這個 handler 之前就把視窗關掉了。
                //    不重新開啟的話，按「繼續編修」反而會失去整張表單——正好是我們要修的反面。
                modalVisible = true;
                return;
            }
        }

        modalVisible = false;
        formSnapshot = null;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        // ⚠️ 一律走 FormKeyboardHelper：它會擋掉中文輸入法組字中的 Enter（選字用的那一下），
        // 也讓 Shift+Enter 落回瀏覽器原生的換行。直接比對 args.Key 會誤觸。
        if (FormKeyboardHelper.IsSubmit(args))
        {
            // Task.Delay 不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，
            // Enter 送出時焦點還在欄位裡，不等就會拿到舊值。
            await Task.Delay(200);
            await OnModalOKHandleAsync(new MouseEventArgs());
        }
        else if (FormKeyboardHelper.IsCancel(args))
        {
            await OnModalCancelHandleAsync(new MouseEventArgs());
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    #endregion

    #region 顯示輔助

    private static string StatusCssClass(string status) => status switch
    {
        "已完成" => "todo-view-status-completed",
        "進行中" => "todo-view-status-processing",
        _ => "todo-view-status-pending",
    };

    private static string PriorityCssClass(string priority) => priority switch
    {
        "高" => "todo-view-priority-high",
        "中" => "todo-view-priority-medium",
        _ => "todo-view-priority-low",
    };

    private void NotifySuccess(string description)
    {
        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private void NotifyError(string description)
    {
        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight
        });
    }

    #endregion
}
