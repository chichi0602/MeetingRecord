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

namespace MeetingRecord.Web.Components.Views.Todos;

public partial class TodoViewView
{
    private readonly ILogger<TodoViewView> logger;
    private readonly TodoService todoService;
    private readonly ProjectService projectService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private ITable? table;

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];
    private List<string> selectedCategoryFilters = [];
    private List<string> selectedTeamFilters = [];
    private List<ProjectAdapterModel> availableProjects = [];

    private int selectedProjectFilter;
    private string selectedStatusFilter = string.Empty;

    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<TodoAdapterModel> todoAdapterModels = [];

    private string modalTitle = "待辦事項維護";
    private bool modalVisible;
    private TodoAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private static IReadOnlyList<string> StatusOptions => TodoAdapterModel.StatusOptions;
    private static IReadOnlyList<string> PriorityOptions => TodoAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public TodoViewView(
        ILogger<TodoViewView> logger,
        TodoService todoService,
        ProjectService projectService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.todoService = todoService;
        this.projectService = projectService;
        this.categoryService = categoryService;
        this.teamService = teamService;
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

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();
        availableProjects = await projectService.GetSelectableAsync();

        await ReloadAsync();
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
            CategoryFilters = selectedCategoryFilters.ToList(),
            TeamFilters = selectedTeamFilters.ToList(),
            ProjectFilter = selectedProjectFilter > 0 ? selectedProjectFilter : null,
            StatusFilter = string.IsNullOrWhiteSpace(selectedStatusFilter) ? null : selectedStatusFilter,
        });

        todoAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
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

    private async Task OnCategoryFilterChanged(IEnumerable<string> values)
    {
        selectedCategoryFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnTeamFilterChanged(IEnumerable<string> values)
    {
        selectedTeamFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.Categories = values?.ToList() ?? [];
    }

    private void OnRecordTeamsChanged(IEnumerable<string> values)
    {
        CurrentRecord.Teams = values?.ToList() ?? [];
    }

    private async Task OnTableChange(QueryModel<TodoAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

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
        modalVisible = true;
        logger.LogInformation("Opened create modal for todo.");
        return Task.CompletedTask;
    }

    private Task OnEditAsync(TodoAdapterModel todoAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改待辦事項";
        CurrentRecord = todoAdapterModel.Clone();
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

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = false;
        logger.LogDebug("Todo modal cancelled.");
        return Task.CompletedTask;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await Task.Delay(200);
            await OnModalOKHandleAsync(new MouseEventArgs());
        }
        else if (args.Key == "Escape" || args.Key == "Esc")
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
