using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Client.Pages.Todos;

/// <summary>
/// 待辦事項：資料走 <see cref="TodoApi"/>（規則都在伺服器的 TodoService），按鈕顯示依 <see cref="PermissionView"/>，
/// 能不能刪由 API 依呼叫者算好（<see cref="TodoDto.CanDelete"/>）。
/// </summary>
public partial class TodoPage : IAsyncDisposable
{
    [Inject] private ILogger<TodoPage> logger { get; set; } = default!;

    [Inject] private TodoApi TodoApi { get; set; } = default!;

    [Inject] private PermissionView PermissionView { get; set; } = default!;

    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;

    [Inject] private ModalService modalService { get; set; } = default!;

    [Inject] private MessageService messageService { get; set; } = default!;

    [Inject] private NotificationService notificationService { get; set; } = default!;

    [Inject] private TodoAutoFitInterop AutoFitInterop { get; set; } = default!;

    private ITable? table;

    private List<TodoProjectOptionDto> availableProjects = [];

    private int selectedProjectFilter;
    private string selectedStatusFilter = string.Empty;

    /// <summary>量測用的根元素。<c>todo-auto-fit.js</c> 從這裡往下找表格與面板。</summary>
    private ElementReference rootElement;

    /// <summary>給 JS 回呼用的參照。null 代表還沒掛上觀察者（沒權限時就不會掛）。</summary>
    private DotNetObjectReference<TodoPage>? autoFitReference;

    /// <summary>
    /// 每頁筆數是否還跟著畫面高度走（0.4.86）。
    /// ⚠️ 使用者自己在分頁器上改過每頁筆數之後就關掉，否則他選了 20 筆，下一次量測會立刻把它蓋回 11 筆。
    /// </summary>
    private bool autoFitEnabled = true;

    /// <summary>自動調整列數的控制器狀態。見 <see cref="TodoFitState"/>。</summary>
    private TodoFitState fitState = TodoAutoFit.InitialState;

    private int _pageIndex = 1;

    /// <summary>每頁筆數：量畫面高度算出來的（見 <see cref="TodoAutoFit"/>），這裡的值只是「量到之前」的起手式。</summary>
    private int _pageSize = TodoAutoFit.FallbackRows;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<TodoDto> todoAdapterModels = [];

    private string modalTitle = "待辦事項維護";
    private bool modalVisible;
    private TodoDto CurrentRecord = NewRecord();

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>開啟修改視窗時的期限（1.0.55）：日期跟它不同才出現「改期限原因」。</summary>
    private DateTime? originalDueDate;

    private bool IsDueDateChanged
        => !isNewRecordMode && originalDueDate?.Date != CurrentRecord.DueDate?.Date;

    /// <summary>⚠️ Esc 會同時走 Modal 的 Keyboard 與表單的 onkeydown，兩者都會呼叫取消；這個旗標避免疊出兩個確認視窗。</summary>
    private bool isDiscardConfirming;
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IReadOnlyList<TodoOwnerSummaryDto> ownerSummaries = [];
    private IReadOnlyList<TodoDto> ownerTodos = [];
    private string selectedOwner = string.Empty;

    /// <summary>面板小清單的狀態篩選（0.4.85）。點膠囊切換，只影響右邊那份小清單，左邊的表格不動。</summary>
    private OwnerTodoFilter ownerTodoFilter = OwnerTodoFilter.All;
    private bool ownerPanelCollapsed;

    private string detailTitle = string.Empty;
    private string detailContent = string.Empty;
    private bool detailVisible;

    private TodoOwnerSummaryDto? SelectedOwnerSummary
        => ownerSummaries.FirstOrDefault(x => x.Owner == selectedOwner);

    /// <summary>套上膠囊篩選之後的小清單。⚠️ 刻意是計算屬性，每條重載路徑都不必記得重套篩選。</summary>
    private IReadOnlyList<TodoDto> FilteredOwnerTodos
        => TodoOwnerFilter.Apply(ownerTodos, ownerTodoFilter);

    /// <summary>面板標題旁的範圍說明。面板只跟專案過濾連動，所以要讓使用者看得出目前算的是哪個範圍。</summary>
    private string OwnerPanelScopeText
        => selectedProjectFilter > 0
            ? availableProjects.FirstOrDefault(x => x.Id == selectedProjectFilter)?.Title ?? "全部專案"
            : "全部專案";

    private static IReadOnlyList<string> StatusOptions => TodoRules.StatusOptions;
    private static IReadOnlyList<string> PriorityOptions => TodoRules.PriorityOptions;

    private bool CanCreate => PermissionView.Can(MagicObjectHelper.角色_待辦事項, PermissionActions.Create);

    private bool CanEdit => PermissionView.Can(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit);

    private static TodoDto NewRecord() => new()
    {
        Id = 0,
        Status = TodoRules.StatusOptions[0],
        Priority = TodoRules.PriorityOptions[1],
    };

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing todo management page.");
        await PermissionView.LoadAsync();
        if (!PermissionView.CanAccessPage(MagicObjectHelper.角色_待辦事項))
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/todos");
            return;
        }

        await LoadProjectsAsync();
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

    /// <summary>JS 量完畫面之後回報（0.4.86）。這是回授：每調整一次就再量一次，直到剩餘空間連一列都放不下。</summary>
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

        await InvokeAsync(async () =>
        {
            _pageSize = decision.PageSize;

            // 頁碼刻意不歸 1：使用者只是拉了視窗大小，不該被丟回第一頁。
            await ReloadAsync();

            // ⚠️ 一定要再量一次。ResizeObserver 只看寬度，而我們剛剛只改了高度。
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

    private async Task LoadProjectsAsync()
        => availableProjects = (await TodoApi.GetSelectableProjectsAsync()).Data ?? [];

    public async Task ReloadAsync()
    {
        var result = await TodoApi.TreeAsync(new TodoTreeRequestDto
        {
            Keyword = searchText,
            SortBy = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : null,
            PageIndex = _pageIndex,
            PageSize = _pageSize,
            ProjectId = selectedProjectFilter > 0 ? selectedProjectFilter : null,
            ProjectScope = projectScope.ToString(),
            Status = string.IsNullOrWhiteSpace(selectedStatusFilter) ? null : selectedStatusFilter,
        });

        todoAdapterModels = result.Data?.Items ?? [];
        _total = result.Data?.TotalCount ?? 0;

        // 1.0.53：勾選依 Id 對回新的列（含子項目），已不能刪的拿掉；換頁、篩選、搜尋由各自的處理先清掉。
        var selectedIds = selectedRows.Select(x => x.Id).ToHashSet();
        selectedRows = TodoBatchSelection.Flatten(todoAdapterModels).Where(x => selectedIds.Contains(x.Id) && CanDelete(x)).ToList();

        // 頁碼落在最後一頁之後（例如刪掉該頁唯一一筆）就夾回最後一頁重載。⚠️ Math.Max(1, ...) 不可省。
        if (todoAdapterModels.Count == 0 && _total > 0 && _pageIndex > 1)
        {
            _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            await ReloadAsync();
            return;
        }

        // 面板與清單一律同進同出：所有變更資料的路徑都只呼叫 ReloadAsync。
        await ReloadOwnerPanelAsync();
        StateHasChanged();
    }

    #region 過濾與排序

    #region 已完成＝封存（1.0.46）

    /// <summary>「使用中｜已完成｜全部」，預設使用中：已完成專案的待辦預設不列出。每次開頁都回到使用中。</summary>
    private ProjectScope projectScope = ProjectScope.Active;

    /// <summary>專案篩選下拉：跟著檢視列出對應的專案。</summary>
    private List<TodoProjectOptionDto> ScopedProjects
        => availableProjects.Where(p => ProjectScopeRules.Matches(p.Status, projectScope)).ToList();

    /// <summary>新增／編輯表單的專案選單：不列封存專案（正在編輯的那筆原本就在封存專案時例外，避免顯示空白）。</summary>
    private List<TodoProjectOptionDto> FormProjects
        => availableProjects.Where(p => !p.IsArchived || p.Id == CurrentRecord.ProjectId).ToList();

    /// <summary>封存專案的待辦唯讀；管理者不受限（全站慣例）。</summary>
    private bool IsReadOnly(TodoDto todo) => todo.IsProjectArchived && !PermissionView.IsAdmin;

    /// <summary>刪除只限專案負責人與管理者（甲方另外可刪自己建立的）；API 依呼叫者算好，服務層另外把關。</summary>
    private static bool CanDelete(TodoDto todo) => todo.CanDelete;

    private async Task OnProjectScopeChangedAsync(ProjectScope scope)
    {
        projectScope = scope;

        // 篩選中的專案不在新的檢視範圍裡，就清掉篩選，不然清單會是空的而且看不出為什麼。
        if (selectedProjectFilter > 0 && ScopedProjects.All(p => p.Id != selectedProjectFilter))
        {
            selectedProjectFilter = 0;
        }

        _pageIndex = 1;
        selectedRows = [];
        await ReloadAsync();
    }

    #endregion

    private async Task OnProjectFilterChanged(int value)
    {
        selectedProjectFilter = value;
        _pageIndex = 1;
        selectedRows = [];
        await ReloadAsync();
    }

    private async Task OnStatusFilterChanged(string value)
    {
        selectedStatusFilter = value ?? string.Empty;
        _pageIndex = 1;
        selectedRows = [];
        await ReloadAsync();
    }

    /// <summary>重載右側負責人面板。刻意只帶專案過濾與檢視範圍——不帶狀態與關鍵字。</summary>
    private async Task ReloadOwnerPanelAsync()
    {
        var projectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : (int?)null;
        ownerSummaries = (await TodoApi.GetOwnersAsync(projectFilter, projectScope.ToString())).Data ?? [];

        // 換專案後原本選中的人可能在新範圍裡沒有任何待辦，退回第一位而不是留一個空面板。
        if (ownerSummaries.All(x => x.Owner != selectedOwner))
        {
            selectedOwner = ownerSummaries.FirstOrDefault()?.Owner ?? string.Empty;
        }

        ownerTodos = string.IsNullOrEmpty(selectedOwner)
            ? []
            : (await TodoApi.GetByOwnerAsync(selectedOwner, projectFilter, projectScope.ToString())).Data ?? [];

        ClampOwnerTodoFilter();
    }

    /// <summary>四顆膠囊的資料來源。逾期那顆維持「0 筆就不渲染」的既有行為（0.4.66）。</summary>
    private static IEnumerable<(OwnerTodoFilter Filter, string Label, int Count, string CssClass)> OwnerStatPills(
        TodoOwnerSummaryDto summary)
    {
        yield return (OwnerTodoFilter.Pending, TodoOwnerFilter.Describe(OwnerTodoFilter.Pending), summary.Pending, "todo-view-status-pending");
        yield return (OwnerTodoFilter.InProgress, TodoOwnerFilter.Describe(OwnerTodoFilter.InProgress), summary.InProgress, "todo-view-status-processing");
        yield return (OwnerTodoFilter.Completed, TodoOwnerFilter.Describe(OwnerTodoFilter.Completed), summary.Completed, "todo-view-status-completed");

        if (summary.Overdue > 0)
        {
            yield return (OwnerTodoFilter.Overdue, TodoOwnerFilter.Describe(OwnerTodoFilter.Overdue), summary.Overdue, "todo-view-overdue");
        }
    }

    /// <summary>點膠囊＝篩「這個人 ＋ 這個狀態」，再點同一顆回到全部。刻意不重撈資料、互斥單選。</summary>
    private void ToggleOwnerTodoFilter(OwnerTodoFilter filter)
        => ownerTodoFilter = ownerTodoFilter == filter ? OwnerTodoFilter.All : filter;

    private void ClearOwnerTodoFilter() => ownerTodoFilter = OwnerTodoFilter.All;

    /// <summary>收合／展開右側面板。刻意不記在 localStorage——這是檢視偏好，重新整理回到展開是可預期的。</summary>
    private void ToggleOwnerPanel() => ownerPanelCollapsed = !ownerPanelCollapsed;

    private async Task OnOwnerChangedAsync(string owner)
    {
        selectedOwner = owner ?? string.Empty;

        var projectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : (int?)null;
        ownerTodos = string.IsNullOrEmpty(selectedOwner)
            ? []
            : (await TodoApi.GetByOwnerAsync(selectedOwner, projectFilter, projectScope.ToString())).Data ?? [];

        // 換人刻意不重設篩選（典型用法是跨人巡同一個狀態），對新的人沒意義的篩選由 clamp 收掉。
        ClampOwnerTodoFilter();
    }

    /// <summary>
    /// 把作用中的篩選收斂回合法狀態：選中的膠囊變 0 筆時回到「全部」。
    /// ⚠️ 「換專案要重設篩選」只能放在 <see cref="OnProjectFilterChanged"/>，絕不能放進 <c>ReloadOwnerPanelAsync</c>。
    /// </summary>
    private void ClampOwnerTodoFilter()
        => ownerTodoFilter = TodoOwnerFilter.Clamp(SelectedOwnerSummary, ownerTodoFilter);

    /// <summary>開啟待辦內容的唯讀檢視。清單只顯示大綱，完整描述在這裡看。</summary>
    private void OpenDetail(TodoDto todo)
    {
        detailTitle = todo.Title;
        detailContent = todo.Description ?? string.Empty;
        detailVisible = true;
    }

    private void OnDetailCancel(MouseEventArgs args) => detailVisible = false;

    #region 異動紀錄與深連結（1.0.44）

    /// <summary>
    /// 從鈴鐺通知點進來時帶的待辦（<c>/todos?todoId=N</c>）：切到它的專案並打開它的異動紀錄。
    /// 待辦頁已經開著時再點另一則通知，元件不會重建、只會重新設參數，所以在 OnParametersSet 處理。
    /// </summary>
    [SupplyParameterFromQuery(Name = "todoId")]
    public int? TodoId { get; set; }

    private int? handledTodoId;

    private bool changesVisible;
    private bool changesLoading;
    private string changesTitle = "異動紀錄";
    private List<TodoChangeDto> changes = [];

    protected override async Task OnParametersSetAsync()
    {
        // 第一次進頁面時 OnInitialized 還沒載完權限，這裡也會先跑：等權限判斷完才處理深連結。
        await PermissionView.LoadAsync();
        if (TodoId is not { } id || id <= 0 || id == handledTodoId || !PermissionView.CanAccessPage(MagicObjectHelper.角色_待辦事項))
        {
            return;
        }

        handledTodoId = id;
        var todo = (await TodoApi.GetAsync(id)).Data;
        if (todo is null || (todo.Id ?? 0) == 0)
        {
            _ = messageService.WarningAsync("找不到這筆待辦，可能已經被刪除或你沒有權限看。");
            return;
        }

        // 切到它所屬的專案，清單上才找得到它；1.0.46：封存專案的待辦在「使用中」看不到，切到全部。
        var scopeMismatch = !ProjectScopeRules.Matches(todo.ProjectStatus, projectScope);
        if (selectedProjectFilter != todo.ProjectId || scopeMismatch)
        {
            selectedProjectFilter = todo.ProjectId;
            if (scopeMismatch)
            {
                projectScope = ProjectScope.All;
            }

            _pageIndex = 1;
            selectedRows = [];
            await ReloadAsync();
        }

        await OpenChangesAsync(todo.Id ?? 0, todo.Title);
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
            changes = (await TodoApi.GetChangesAsync(todoId)).Data ?? [];
        }
        finally
        {
            changesLoading = false;
        }
    }

    private void OnChangesCancel(MouseEventArgs args) => changesVisible = false;

    #endregion

    private async Task OnTableChange(QueryModel<TodoDto> args)
    {
        // 全選只算這一頁（1.0.53）：換頁、排序就清掉勾選。
        if (args.PageIndex != _pageIndex || args.PageSize != _pageSize)
        {
            selectedRows = [];
        }

        _pageIndex = args.PageIndex;

        // 回報的筆數跟我們最後設的不同，只可能是使用者自己在分頁器上選的：從這一刻起不再自動調整（0.4.86）。
        if (autoFitEnabled && args.PageSize != _pageSize)
        {
            autoFitEnabled = false;
        }

        _pageSize = args.PageSize;

        // 只有真的選了方向才送排序欄位（1.1.19）：表格在沒有排序時也會回報某一欄、方向為「無」，
        // 服務層看到欄位就跳過預設排序（有截止日的在前），方向又是「無」而完全不排，分頁順序變成不固定。
        if (args.SortModel?.Any(model => HasSortDirection(model.SortDirection)) == true)
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

        await ReloadAsync();
    }

    private static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
        => sortModels.FirstOrDefault(model => HasSortDirection(model.SortDirection)) ?? sortModels.Last();

    private static bool HasSortDirection(SortDirection sortDirection)
        => sortDirection == SortDirection.Ascending || sortDirection == SortDirection.Descending;

    private static string ResolveSortFieldName(ITableSortModel sortModel) => sortModel.FieldName ?? string.Empty;

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        selectedRows = [];
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await LoadProjectsAsync();
        await ReloadAsync();
        NotifySuccess("已更新最新資料");
    }

    #endregion

    #region 新增 / 修改 / 刪除 / 完成

    /// <summary>清單上的勾選框。完成與否只寫 Status 一個欄位。</summary>
    private async Task OnCompletedChangedAsync(TodoDto todo, bool isCompleted)
    {
        // 勾選框沒權限時只是停用，處理器仍掛著；這裡再擋一次（0.4.115）。
        if (!CanEdit)
        {
            return;
        }

        var result = await TodoApi.SetCompletedAsync(todo.Id ?? 0, isCompleted);
        if (!result.Success && result.StatusCode != 401)
        {
            NotifyError(TodoApi.ErrorText(result));
        }

        await ReloadAsync();
    }

    private async Task OnAddAsync()
    {
        // 開表單前重載可選專案（0.4.115）：有人替你加了協作團隊後，這裡沒更新的話「所屬專案」會是空白。
        await LoadProjectsAsync();
        CurrentRecord = NewRecord();
        // 已經套用專案過濾時，新增的待辦預設落在同一個專案；但不會落在已完成（封存）的專案（1.0.46）。
        CurrentRecord.ProjectId = availableProjects.Any(p => p.Id == selectedProjectFilter && !p.IsArchived)
            ? selectedProjectFilter
            : availableProjects.FirstOrDefault(p => !p.IsArchived)?.Id ?? 0;

        isNewRecordMode = true;
        modalTitle = "新增待辦事項";
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
    }

    #region 子項目（1.0.45）

    /// <summary>編輯視窗「屬於（主待辦）」的選項：同專案、本身不是子項目、未完成、不是自己。</summary>
    private List<TodoParentOptionDto> parentCandidates = [];

    private async Task LoadParentCandidatesAsync()
    {
        parentCandidates = CurrentRecord.ProjectId > 0
            ? (await TodoApi.GetParentCandidatesAsync(CurrentRecord.ProjectId, CurrentRecord.Id is > 0 ? CurrentRecord.Id : null)).Data ?? []
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
    private async Task OnAddSubtaskAsync(TodoDto parent)
    {
        await LoadProjectsAsync();
        CurrentRecord = NewRecord();
        CurrentRecord.ProjectId = parent.ProjectId;
        CurrentRecord.ParentTodoId = parent.Id;

        isNewRecordMode = true;
        modalTitle = $"新增子項目 — {parent.Title}";
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
    }

    #endregion

    private async Task OnEditAsync(TodoDto todo)
    {
        await LoadProjectsAsync();
        isNewRecordMode = false;
        modalTitle = "修改待辦事項";
        CurrentRecord = todo.Clone();
        CurrentRecord.DueDateChangeNote = null;
        originalDueDate = todo.DueDate;
        await LoadParentCandidatesAsync();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
    }

    #region 多選刪除（1.0.53）

    /// <summary>目前這一頁勾選的列（主待辦與展開的子項目）。換頁、搜尋、篩選時清掉；重新載入時依 Id 保留。</summary>
    private IEnumerable<TodoDto> selectedRows = [];

    private bool isBatchDeleting;

    /// <summary>
    /// 有待辦「編輯」權限才顯示勾選欄（刪除要求的就是 edit）。
    /// ⚠️ 1.0.59：只看權限，**不可以看資料**——AntDesign 1.6 的 Selection 在勾選欄一下有一下沒有時會丟 NullReferenceException。
    /// 不能刪的列靠 Disabled 擋；TableSelectionColumnTests 會擋回頭路。
    /// </summary>
    private bool ShowSelection => PermissionView.Can(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit);

    private async Task OnBatchDeleteAsync()
    {
        // 主待辦已勾選時略過它被勾的子項目：刪主待辦會連子項目一起刪。
        var items = TodoBatchSelection.ExcludeChildrenOfSelectedParents(selectedRows.Where(CanDelete));
        if (items.Count == 0 || isBatchDeleting)
        {
            return;
        }

        var childCount = items.Where(x => !x.IsSubtask).Sum(x => x.ChildrenTotal);
        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            Content = childCount > 0
                ? $"確定要刪除勾選的 {items.Count} 筆待辦事項嗎？它們底下的 {childCount} 個子項目也會一起刪除。此操作無法復原。"
                : $"確定要刪除勾選的 {items.Count} 筆待辦事項嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });
        if (!ok)
        {
            return;
        }

        isBatchDeleting = true;
        try
        {
            var result = await BatchRunner.RunAsync(items, x => x.Title, async x =>
            {
                var r = await TodoApi.DeleteAsync(x.Id ?? 0);
                return r.Success ? null : TodoApi.ErrorText(r);
            });
            BatchResultNotifier.Notify(notificationService, result, "刪除");
        }
        finally
        {
            isBatchDeleting = false;
        }

        selectedRows = [];
        await ReloadAsync();
    }

    #endregion

    private async Task OnDeleteAsync(TodoDto todo)
    {
        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            // 1.0.45：主待辦連子項目一起刪，要先講清楚。
            Content = todo.ChildrenTotal > 0
                ? $"確定要刪除這筆待辦事項嗎？它底下的 {todo.ChildrenTotal} 個子項目也會一起刪除。此操作無法復原。"
                : "確定要刪除這筆待辦事項嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            return;
        }

        var result = await TodoApi.DeleteAsync(todo.Id ?? 0);
        if (!result.Success)
        {
            if (result.StatusCode != 401)
            {
                NotifyError(TodoApi.ErrorText(result));
            }

            return;
        }

        NotifySuccess("刪除成功");
        await ReloadAsync();
    }

    /// <summary>表單送出閘門（1.0.62）：「確定」與 Enter 共用，等候也算在送出中。</summary>
    private readonly FormSubmitGate submitGate = new();

    /// <summary>存檔中（含 Enter 等候回寫）。</summary>
    private bool isSaving => submitGate.IsBusy;

    private Task OnModalOKHandleAsync(MouseEventArgs args) => submitGate.RunAsync(SaveModalAsync);

    private async Task SaveModalAsync()
    {
        // 1.1.34：視窗已經關了就不再存。存檔成功時「關窗」與「不再存檔中」在同一次重繪才生效，
        // 關窗動畫期間「確定」又按得到，沒有這行同一筆會再存一次（新增兩筆）。
        if (!modalVisible)
        {
            return;
        }

        if (LocalEditContext?.Validate() == false)
        {
            foreach (var error in LocalEditContext.GetValidationMessages())
            {
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

        ApiResult<object> updateResult = new() { Success = true };
        ApiResult<TodoDto> createResult = new() { Success = true };
        if (isNewRecordMode)
        {
            CurrentRecord.Id = 0;
            createResult = await TodoApi.CreateAsync(CurrentRecord);
        }
        else
        {
            updateResult = await TodoApi.UpdateAsync(CurrentRecord);
        }

        var success = isNewRecordMode ? createResult.Success : updateResult.Success;
        if (!success)
        {
            var statusCode = isNewRecordMode ? createResult.StatusCode : updateResult.StatusCode;
            if (statusCode != 401)
            {
                NotifyError(isNewRecordMode ? TodoApi.ErrorText(createResult) : TodoApi.ErrorText(updateResult));
            }

            modalVisible = true;
            return;
        }

        // 先關窗再通知與重新載入（1.1.19）：WASM 頁重新載入是好幾趟 HTTP，視窗留到載完才關的話，
        // 看到「新增成功」就去按工具列的人，點擊會被還沒關掉的視窗吃掉。
        modalVisible = false;
        NotifySuccess(isNewRecordMode ? "新增成功" : "修改成功");
        if (isNewRecordMode)
        {
            _ = messageService.SuccessAsync("新增成功");
        }

        await ReloadAsync();
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
                // 開關由本頁決定（1.1.19 起視窗是單向繫結）：按「繼續編修」就維持開著。
                modalVisible = true;
                return;
            }
        }

        modalVisible = false;
        formSnapshot = null;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        // ⚠️ 一律走 FormKeyboardHelper：它會擋掉中文輸入法組字中的 Enter，也讓 Shift+Enter 落回原生換行。
        if (FormKeyboardHelper.IsSubmit(args))
        {
            // 等候不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，Enter 送出時焦點還在欄位裡。
            await submitGate.RunAsync(SaveModalAsync, FormSubmitGate.EnterSettleDelay);
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
