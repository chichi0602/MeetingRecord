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
            logger.LogWarning("Todo view denied because current user has not this role permission.");
            return;
        }

        availableProjects = await projectService.GetSelectableAsync();

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
        ownerSummaries = await todoService.GetOwnerSummariesAsync(projectFilter);

        // 換專案後原本選中的人可能在新範圍裡沒有任何待辦，退回第一位而不是留一個空面板。
        if (ownerSummaries.All(x => x.Owner != selectedOwner))
        {
            selectedOwner = ownerSummaries.FirstOrDefault()?.Owner ?? string.Empty;
        }

        ownerTodos = string.IsNullOrEmpty(selectedOwner)
            ? []
            : await todoService.GetByOwnerAsync(selectedOwner, projectFilter);

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
            : await todoService.GetByOwnerAsync(selectedOwner, projectFilter);

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
        availableProjects = await projectService.GetSelectableAsync();
        await ReloadAsync();
        NotifySuccess("已更新最新資料");
    }

    #endregion

    #region 新增 / 修改 / 刪除 / 完成

    /// <summary>清單上的勾選框。完成與否只寫 Status 一個欄位。</summary>
    private async Task OnCompletedChangedAsync(TodoAdapterModel todo, bool isCompleted)
    {
        var result = await todoService.SetCompletedAsync(todo.Id, isCompleted);
        if (!result.Success)
        {
            NotifyError(result.Message);
        }

        await ReloadAsync();
    }

    private Task OnAddAsync()
    {
        CurrentRecord = new TodoAdapterModel
        {
            Status = StatusOptions[0],
            Priority = PriorityOptions[1],
            // 已經套用專案過濾時，新增的待辦預設落在同一個專案，省一次選取。
            ProjectId = selectedProjectFilter > 0
                ? selectedProjectFilter
                : availableProjects.FirstOrDefault()?.Id ?? 0,
        };

        isNewRecordMode = true;
        modalTitle = "新增待辦事項";
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        logger.LogInformation("Opened create modal for todo.");
        return Task.CompletedTask;
    }

    private Task OnEditAsync(TodoAdapterModel todoAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改待辦事項";
        CurrentRecord = todoAdapterModel.Clone();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        logger.LogInformation("Opened edit modal for todo. TodoId={TodoId}", todoAdapterModel.Id);
        return Task.CompletedTask;
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
            Content = "確定要刪除這筆待辦事項嗎？此操作無法復原。",
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

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
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
