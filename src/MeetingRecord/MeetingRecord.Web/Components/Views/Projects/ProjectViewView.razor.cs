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

namespace MeetingRecord.Web.Components.Views.Projects;

public partial class ProjectViewView
{
    private readonly ILogger<ProjectViewView> logger;
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
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<ProjectAdapterModel> projectAdapterModels = [];
    private readonly List<PendingUploadFileItem> pendingUploadFiles = [];
    private readonly HashSet<int> removedFileIds = [];

    private string modalTitle = "專案維護";
    private bool modalVisible;
    private ProjectAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IReadOnlyList<string> StatusOptions => ProjectAdapterModel.StatusOptions;
    private IReadOnlyList<string> PriorityOptions => ProjectAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public ProjectViewView(
        ILogger<ProjectViewView> logger,
        ProjectService projectService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing project management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Project management view initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_專案管理) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Project management view denied because current user has not this role permission.");
            return;
        }

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading projects. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        DataRequestResult<ProjectAdapterModel> dataRequestResult = await projectService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
            CategoryFilters = selectedCategoryFilters.ToList(),
            TeamFilters = selectedTeamFilters.ToList(),
        });

        projectAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
        logger.LogInformation("Project list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
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

    private async Task OnTableChange(QueryModel<ProjectAdapterModel> args)
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

        logger.LogDebug("Project table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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
        logger.LogInformation("Project search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Project refresh triggered.");
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = "已更新最新資料",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private async Task OnEditAsync(ProjectAdapterModel projectAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改專案";
        CurrentRecord = await projectService.GetAsync(projectAdapterModel.Id);
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for project. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);
    }

    private async Task OnDeleteAsync(ProjectAdapterModel projectAdapterModel)
    {
        logger.LogInformation("Delete project requested. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);

        var beforeDeleteCheckResult = await projectService.BeforeDeleteCheckAsync(projectAdapterModel);
        if (!beforeDeleteCheckResult.Success)
        {
            logger.LogWarning("Project delete pre-check failed. ProjectId={ProjectId}, Message={Message}", projectAdapterModel.Id, beforeDeleteCheckResult.Message);
            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = beforeDeleteCheckResult.Message,
                NotificationType = NotificationType.Error,
                Placement = NotificationPlacement.BottomRight
            });
            return;
        }

        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            Content = "確定要刪除這筆紀錄嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Project delete cancelled by user. ProjectId={ProjectId}", projectAdapterModel.Id);
            return;
        }

        await projectService.DeleteAsync(projectAdapterModel.Id);
        logger.LogInformation("Project delete completed. ProjectId={ProjectId}", projectAdapterModel.Id);

        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = "刪除成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        await ReloadAsync();
    }

    private Task OnAddAsync(bool continueOnCapturedContext)
    {
        CurrentRecord = new ProjectAdapterModel
        {
            Status = StatusOptions.First(),
            Priority = PriorityOptions[1],
            CompletionPercentage = 0,
            Files = []
        };

        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        isNewRecordMode = true;
        modalTitle = "新增專案";
        modalVisible = true;
        logger.LogInformation("Opened create modal for project.");
        return Task.CompletedTask;
    }

    private async Task OnProjectFilesSelectedAsync(InputFileChangeEventArgs args)
    {
        foreach (var file in args.GetMultipleFiles(1000))
        {
            if (file.Size > ProjectService.MaxUploadFileSize)
            {
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "檔案過大",
                    Description = $"{file.Name} 超過 1GB 限制",
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });
                continue;
            }

            pendingUploadFiles.Add(new PendingUploadFileItem
            {
                Id = Guid.NewGuid(),
                File = file
            });
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
            foreach (var error in allErrors)
            {
                logger.LogWarning("Project form validation failed. Error={Error}", error);
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

        var uploadInputs = new List<ProjectUploadFileInput>();
        var uploadStreams = new List<Stream>();

        try
        {
            foreach (var pendingUploadFile in pendingUploadFiles)
            {
                var stream = pendingUploadFile.File.OpenReadStream(ProjectService.MaxUploadFileSize);
                uploadStreams.Add(stream);
                uploadInputs.Add(new ProjectUploadFileInput
                {
                    FileName = pendingUploadFile.File.Name,
                    ContentType = pendingUploadFile.File.ContentType,
                    FileSize = pendingUploadFile.File.Size,
                    Content = stream
                });
            }

            VerifyRecordResult actionResult;

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await projectService.BeforeAddCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogWarning("Project create pre-check failed. Title={Title}, Message={Message}", CurrentRecord.Title, beforeAddCheckResult.Message);
                    _ = notificationService.Open(new NotificationConfig
                    {
                        Message = "系統訊息",
                        Description = beforeAddCheckResult.Message,
                        NotificationType = NotificationType.Error,
                        Placement = NotificationPlacement.BottomRight
                    });

                    modalVisible = true;
                    return;
                }

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                actionResult = await projectService.AddAsync(CurrentRecord, uploadInputs);
                logger.LogInformation("Project create submitted. Title={Title}", CurrentRecord.Title);
            }
            else
            {
                var beforeUpdateCheckResult = await projectService.BeforeUpdateCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogWarning("Project update pre-check failed. ProjectId={ProjectId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    _ = notificationService.Open(new NotificationConfig
                    {
                        Message = "系統訊息",
                        Description = beforeUpdateCheckResult.Message,
                        NotificationType = NotificationType.Error,
                        Placement = NotificationPlacement.BottomRight
                    });

                    modalVisible = true;
                    return;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await projectService.UpdateAsync(CurrentRecord, uploadInputs, removedFileIds);
                logger.LogInformation("Project update submitted. ProjectId={ProjectId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            }

            if (!actionResult.Success)
            {
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "系統訊息",
                    Description = actionResult.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });

                modalVisible = true;
                return;
            }

            pendingUploadFiles.Clear();
            removedFileIds.Clear();

            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = isNewRecordMode ? "新增成功" : "修改成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            modalVisible = false;
        }
        finally
        {
            foreach (var uploadStream in uploadStreams)
            {
                uploadStream.Dispose();
            }
        }
    }

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = false;
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        logger.LogDebug("Project modal cancelled.");
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

    private void RemovePendingFile(Guid fileId)
    {
        var file = pendingUploadFiles.FirstOrDefault(x => x.Id == fileId);
        if (file is not null)
        {
            pendingUploadFiles.Remove(file);
        }
    }

    private void RemoveExistingFile(int fileId)
    {
        var file = CurrentRecord.Files.FirstOrDefault(x => x.Id == fileId);
        if (file is null)
        {
            return;
        }

        removedFileIds.Add(fileId);
        CurrentRecord.Files.Remove(file);
    }

    private static string GetProjectFileDownloadUrl(int fileId)
    {
        return $"/api/project-files/{fileId}/download";
    }

    private static string FormatFileSize(long fileSize)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = fileSize;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private sealed class PendingUploadFileItem
    {
        public Guid Id { get; set; }

        public IBrowserFile File { get; set; } = default!;
    }
}
