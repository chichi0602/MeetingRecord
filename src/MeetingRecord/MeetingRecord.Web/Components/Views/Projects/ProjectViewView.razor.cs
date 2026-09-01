using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Projects;

public partial class ProjectViewView
{
    private readonly ILogger<ProjectViewView> logger;
    private readonly ProjectService projectService;
    private readonly MeetingService meetingService;
    private readonly PromptTemplateService promptTemplateService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];

    private List<ProjectAdapterModel> projects = [];
    private int selectedProjectId;

    private List<MeetingAdapterModel> selectableTranscripts = [];
    private List<MeetingAdapterModel> projectMeetings = [];
    private List<PromptTemplateAdapterModel> promptTemplates = [];

    private int selectedTranscriptId;
    private int selectedPromptTemplateId;
    private bool isGenerating;

    private readonly List<PendingUploadFileItem> pendingUploadFiles = [];
    private readonly HashSet<int> removedFileIds = [];

    private string modalTitle = "專案維護";
    private bool modalVisible;
    private ProjectAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private bool draftViewModalVisible;
    private bool draftEditModalVisible;
    private string draftModalTitle = "會議紀錄";
    private string? draftContent;
    private int draftMeetingId;

    private bool transcriptModalVisible;
    private string transcriptModalTitle = "逐字稿預覽";
    private string? transcriptContent;

    private ProjectAdapterModel? SelectedProject => projects.FirstOrDefault(x => x.Id == selectedProjectId);

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
        MeetingService meetingService,
        PromptTemplateService promptTemplateService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.meetingService = meetingService;
        this.promptTemplateService = promptTemplateService;
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

        // 用葉節點鍵而非群組鍵：工具列與操作鈕都是用 角色_專案項目 判斷，
        // 頁面守門卻用群組鍵會讓兩者不一致（MeetingViewView 用的就是葉節點鍵）。
        // 0.4.33 起「專案管理」群組已從選單移除，更沒有理由用群組鍵。
        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_專案項目) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Project management view denied because current user has not this role permission.");
            return;
        }

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();

        await ReloadAsync();
    }

    #region 載入

    public async Task ReloadAsync()
    {
        projects = await projectService.GetSelectableAsync();

        // 選取的專案被刪掉或已不可存取時，退回第一筆。
        if (projects.All(x => x.Id != selectedProjectId))
        {
            selectedProjectId = projects.FirstOrDefault()?.Id ?? 0;
        }

        await ReloadProjectContextAsync();
        logger.LogInformation("Project list reloaded successfully. Count={Count}", projects.Count);
        StateHasChanged();
    }

    /// <summary>重新載入目前專案底下的資料（歷史會議紀錄、可選逐字稿與提示詞）。</summary>
    private async Task ReloadProjectContextAsync()
    {
        if (selectedProjectId <= 0)
        {
            projectMeetings = [];
            selectableTranscripts = [];
            promptTemplates = [];
            return;
        }

        projectMeetings = await meetingService.GetByProjectAsync(selectedProjectId);
        selectableTranscripts = await meetingService.GetSelectableTranscriptsAsync();
        promptTemplates = await promptTemplateService.GetEnabledSelectableAsync();

        // 選過的逐字稿若已不在可選清單（例如被別的專案取走），清掉避免送出無效請求。
        if (selectableTranscripts.All(x => x.Id != selectedTranscriptId))
        {
            selectedTranscriptId = 0;
        }

        if (promptTemplates.All(x => x.Id != selectedPromptTemplateId))
        {
            selectedPromptTemplateId = promptTemplates.FirstOrDefault()?.Id ?? 0;
        }
    }

    private async Task OnProjectSelectedAsync(int projectId)
    {
        selectedProjectId = projectId;
        selectedTranscriptId = 0;
        logger.LogInformation("Project selection changed. ProjectId={ProjectId}", projectId);
        await ReloadProjectContextAsync();
        StateHasChanged();
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

    #endregion

    #region AI 轉會議紀錄

    /// <summary>已被其他專案取用的逐字稿不可選；屬於本專案的可以重選以更換提示詞重新產生。</summary>
    private bool IsTranscriptDisabled(MeetingAdapterModel transcript)
    {
        return transcript.ProjectId is not null && transcript.ProjectId != selectedProjectId;
    }

    private string DescribeTranscriptOwnership(MeetingAdapterModel transcript)
    {
        if (transcript.ProjectId is null)
        {
            return "未歸屬";
        }

        if (transcript.ProjectId == selectedProjectId)
        {
            return transcript.HasDraft ? "本專案 · 重新產生將覆蓋既有會議紀錄" : "本專案";
        }

        return $"已屬：{transcript.ProjectTitle}";
    }

    private async Task OnGenerateDraftAsync()
    {
        if (selectedProjectId <= 0 || selectedTranscriptId <= 0 || selectedPromptTemplateId <= 0)
        {
            return;
        }

        var transcript = selectableTranscripts.FirstOrDefault(x => x.Id == selectedTranscriptId);
        if (transcript is not null && transcript.HasDraft)
        {
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認重新產生",
                Content = "這份逐字稿已經產生過會議紀錄，重新產生會覆蓋既有內容（包含人工編修過的部分）。確定要繼續嗎？",
                OkText = "重新產生",
                CancelText = "取消",
                MaskClosable = false
            });

            if (!confirmed)
            {
                logger.LogDebug("Draft regeneration cancelled by user. MeetingId={MeetingId}", selectedTranscriptId);
                return;
            }
        }

        isGenerating = true;
        try
        {
            logger.LogInformation(
                "Draft generation requested. MeetingId={MeetingId}, ProjectId={ProjectId}, PromptTemplateId={PromptTemplateId}",
                selectedTranscriptId,
                selectedProjectId,
                selectedPromptTemplateId);

            var result = await meetingService.RequestDraftAsync(
                selectedTranscriptId,
                selectedProjectId,
                selectedPromptTemplateId);

            if (!result.Success)
            {
                NotifyError(result.Message);
                return;
            }

            NotifySuccess("已排入生成佇列，請稍後重新整理查看結果。");
            await ReloadProjectContextAsync();
        }
        finally
        {
            isGenerating = false;
        }
    }

    private Task OnViewDraftAsync(MeetingAdapterModel meeting)
    {
        draftMeetingId = meeting.Id;
        draftModalTitle = $"會議紀錄 - {meeting.Title}";
        draftContent = meeting.DraftContent;
        draftViewModalVisible = true;
        return Task.CompletedTask;
    }

    private Task OnEditDraftAsync(MeetingAdapterModel meeting)
    {
        draftMeetingId = meeting.Id;
        draftModalTitle = $"編修會議紀錄 - {meeting.Title}";
        draftContent = meeting.DraftContent;
        draftEditModalVisible = true;
        return Task.CompletedTask;
    }

    private Task OnDraftViewModalCancelAsync(MouseEventArgs args)
    {
        draftViewModalVisible = false;
        return Task.CompletedTask;
    }

    private async Task OnDraftEditModalOkAsync(MouseEventArgs args)
    {
        var result = await meetingService.UpdateDraftAsync(draftMeetingId, draftContent);
        if (!result.Success)
        {
            NotifyError(result.Message);
            draftEditModalVisible = true;
            return;
        }

        NotifySuccess("會議紀錄已儲存。");
        draftEditModalVisible = false;
        await ReloadProjectContextAsync();
    }

    private Task OnDraftEditModalCancelAsync(MouseEventArgs args)
    {
        draftEditModalVisible = false;
        return Task.CompletedTask;
    }

    private Task OnTranscriptModalCancelAsync(MouseEventArgs args)
    {
        transcriptModalVisible = false;
        return Task.CompletedTask;
    }

    private async Task OnPreviewTranscriptAsync(MeetingAdapterModel meeting)
    {
        transcriptModalTitle = $"逐字稿預覽 - {meeting.Title}";
        transcriptContent = await meetingService.ReadTranscriptAsync(meeting.Id);

        if (string.IsNullOrWhiteSpace(transcriptContent))
        {
            NotifyError("找不到逐字稿內容，或沒有權限檢視。");
            return;
        }

        transcriptModalVisible = true;
    }

    #endregion

    #region 專案維護

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.Categories = values?.ToList() ?? [];
    }

    private void OnRecordTeamsChanged(IEnumerable<string> values)
    {
        CurrentRecord.Teams = values?.ToList() ?? [];
    }

    private async Task OnEditSelectedAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        isNewRecordMode = false;
        modalTitle = "修改專案";
        CurrentRecord = await projectService.GetAsync(SelectedProject.Id);
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for project. ProjectId={ProjectId}", CurrentRecord.Id);
    }

    private async Task OnDeleteSelectedAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        var project = SelectedProject;
        logger.LogInformation("Delete project requested. ProjectId={ProjectId}, Title={Title}", project.Id, project.Title);

        var beforeDeleteCheckResult = await projectService.BeforeDeleteCheckAsync(project);
        if (!beforeDeleteCheckResult.Success)
        {
            logger.LogWarning("Project delete pre-check failed. ProjectId={ProjectId}, Message={Message}", project.Id, beforeDeleteCheckResult.Message);
            NotifyError(beforeDeleteCheckResult.Message);
            return;
        }

        var content = projectMeetings.Count > 0
            ? $"確定要刪除「{project.Title}」嗎？此操作無法復原。底下 {projectMeetings.Count} 份會議紀錄不會被刪除，只會解除歸屬。"
            : $"確定要刪除「{project.Title}」嗎？此操作無法復原。";

        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            Content = content,
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Project delete cancelled by user. ProjectId={ProjectId}", project.Id);
            return;
        }

        await projectService.DeleteAsync(project.Id);
        logger.LogInformation("Project delete completed. ProjectId={ProjectId}", project.Id);

        selectedProjectId = 0;
        NotifySuccess("刪除成功");
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
                    NotifyError(beforeAddCheckResult.Message);
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
                    NotifyError(beforeUpdateCheckResult.Message);
                    modalVisible = true;
                    return;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await projectService.UpdateAsync(CurrentRecord, uploadInputs, removedFileIds);
                logger.LogInformation("Project update submitted. ProjectId={ProjectId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            }

            if (!actionResult.Success)
            {
                NotifyError(actionResult.Message);
                modalVisible = true;
                return;
            }

            pendingUploadFiles.Clear();
            removedFileIds.Clear();

            NotifySuccess(isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
                // 新增後把選取切到剛建立的專案，使用者才不用自己再選一次。
                await ReloadAsync();
                selectedProjectId = projects.FirstOrDefault(x => x.Title == CurrentRecord.Title)?.Id ?? selectedProjectId;
                await ReloadProjectContextAsync();
            }
            else
            {
                await ReloadAsync();
            }

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

    #endregion

    #region 顯示輔助

    private static string FormatDateRange(DateTime? startDate, DateTime? endDate)
    {
        if (startDate is null && endDate is null)
        {
            return "未設定";
        }

        var start = startDate?.ToString("yyyy/MM/dd") ?? "未設定";
        var end = endDate?.ToString("yyyy/MM/dd") ?? "未設定";
        return $"{start} – {end}";
    }

    private static string DraftStatusCssClass(DraftStatus status) => status switch
    {
        DraftStatus.Completed => "project-view-status-completed",
        DraftStatus.Processing => "project-view-status-processing",
        DraftStatus.Pending => "project-view-status-pending",
        DraftStatus.Failed => "project-view-status-failed",
        _ => "project-view-status-none",
    };

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

    private sealed class PendingUploadFileItem
    {
        public Guid Id { get; set; }

        public IBrowserFile File { get; set; } = default!;
    }
}
