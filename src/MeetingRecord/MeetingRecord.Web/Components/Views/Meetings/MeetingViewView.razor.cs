using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Meetings;

public partial class MeetingViewView : IDisposable
{
    private readonly ILogger<MeetingViewView> logger;
    private readonly MeetingService meetingService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private ITable? table;

    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    /// <summary>已因「轉錄結束」重新載入過的會議 Id，避免同一筆重複觸發重載。</summary>
    private readonly HashSet<int> reloadedFinishedMeetingIds = [];

    private List<MeetingAdapterModel> meetingAdapterModels = [];

    private string modalTitle = "會議紀錄維護";
    private bool modalVisible;
    private MeetingAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IBrowserFile? pendingMediaFile;
    private bool isUploading;
    private int uploadPercent;

    private bool transcriptModalVisible;
    private string transcriptModalTitle = "逐字稿";
    private string transcriptContent = string.Empty;

    /// <summary>目前開著的逐字稿屬於哪一筆會議；儲存時要用。</summary>
    private int editingTranscriptMeetingId;
    private bool canEditTranscript;
    private bool isSavingTranscript;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public MeetingViewView(
        ILogger<MeetingViewView> logger,
        MeetingService meetingService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService,
        ITranscriptionProgressNotifier progressNotifier)
    {
        this.logger = logger;
        this.meetingService = meetingService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
        this.progressNotifier = progressNotifier;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing meeting management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Meeting view initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_會議紀錄) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Meeting view denied because current user has not this role permission.");
            return;
        }

        progressNotifier.Changed += OnTranscriptionProgressChanged;

        await ReloadAsync();
    }

    /// <summary>
    /// 轉錄進度變動時更新「轉錄狀態」欄。
    ///
    /// <para>
    /// 由背景執行緒引發，必須切回 UI 執行緒。進行中只重繪（百分比來自記憶體，不必查庫）；
    /// 只有工作結束時才重新載入，讓狀態欄從「處理中」翻成資料庫裡的「已完成／失敗」。
    /// 每段都重新查一次資料庫是沒有必要的負擔。
    /// </para>
    /// </summary>
    private void OnTranscriptionProgressChanged()
        => _ = InvokeAsync(async () =>
        {
            // 已完成的項目會留在通知器裡直到使用者關閉，所以要記下已處理過的 Id，
            // 否則之後每一次進度變動都會再重新載入一次。
            var finishedIds = progressNotifier
                .GetSnapshot()
                .Where(x => x.Phase is TranscriptionPhase.Completed or TranscriptionPhase.Failed)
                .Select(x => x.MeetingId)
                .ToList();

            // HashSet.Add 回傳 true 代表這一筆是新完成的。用 Count 而非 Any，
            // 確保整個清單都被走過（Any 會短路，漏掉同時完成的其他筆）。
            var newlyFinishedCount = finishedIds.Count(reloadedFinishedMeetingIds.Add);

            if (newlyFinishedCount > 0)
            {
                await ReloadAsync();
            }
            else
            {
                StateHasChanged();
            }
        });

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading meetings. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        DataRequestResult<MeetingAdapterModel> dataRequestResult = await meetingService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
        });

        meetingAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
        logger.LogInformation("Meeting list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    #region 過濾、排序與分頁

    private async Task OnTableChange(QueryModel<MeetingAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = GetCurrentSortModel(args.SortModel);
            string sortValue = tableSortModel.SortDirection.ToString() ?? string.Empty;
            string resolvedSortField = ResolveSortFieldName(tableSortModel);
            sortDirection = sortValue;
            sortField = resolvedSortField;
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        logger.LogDebug("Meeting table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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
        logger.LogInformation("Meeting search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Meeting refresh triggered.");
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "已更新最新資料",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    #endregion

    #region 新增 / 修改 / 刪除

    private Task OnAddAsync()
    {
        CurrentRecord = new();
        ResetUploadState();
        isNewRecordMode = true;
        modalTitle = "新增會議紀錄";
        modalVisible = true;
        logger.LogInformation("Opened create modal for meeting.");
        return Task.CompletedTask;
    }

    private Task OnEditAsync(MeetingAdapterModel meetingAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改會議紀錄";
        CurrentRecord = meetingAdapterModel.Clone();
        ResetUploadState();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for meeting. MeetingId={MeetingId}, Title={Title}", meetingAdapterModel.Id, meetingAdapterModel.Title);
        return Task.CompletedTask;
    }

    private async Task OnDeleteAsync(MeetingAdapterModel meetingAdapterModel)
    {
        logger.LogInformation("Delete meeting requested. MeetingId={MeetingId}, Title={Title}", meetingAdapterModel.Id, meetingAdapterModel.Title);

        var ok = await modalService.ConfirmAsync(new ConfirmOptions()
        {
            Title = "確認刪除",
            Content = "確定要刪除這筆會議紀錄嗎？影音檔與逐字稿會一併刪除，此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Meeting delete cancelled by user. MeetingId={MeetingId}", meetingAdapterModel.Id);
            return;
        }

        await meetingService.DeleteAsync(meetingAdapterModel.Id);
        logger.LogInformation("Meeting delete completed. MeetingId={MeetingId}", meetingAdapterModel.Id);

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "刪除成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        await ReloadAsync();
    }

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (isUploading)
        {
            return;
        }

        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
            foreach (var error in allErrors)
            {
                logger.LogWarning("Meeting form validation failed. Error={Error}", error);
                _ = notificationService.Open(new NotificationConfig()
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

        if (isNewRecordMode)
        {
            var beforeAddCheckResult = await meetingService.BeforeAddCheckAsync(CurrentRecord);
            if (!beforeAddCheckResult.Success)
            {
                NotifyError(beforeAddCheckResult.Message);
                modalVisible = true;
                return;
            }

            CurrentRecord.CreatedAt = DateTime.Now;
            CurrentRecord.UpdatedAt = DateTime.Now;

            var addResult = await meetingService.AddAsync(CurrentRecord);
            if (!addResult.Success)
            {
                NotifyError(addResult.Message);
                modalVisible = true;
                return;
            }

            logger.LogInformation("Meeting create submitted. MeetingId={MeetingId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            NotifySuccess("新增成功");
            _ = messageService.SuccessAsync("新增成功");
        }
        else
        {
            var beforeUpdateCheckResult = await meetingService.BeforeUpdateCheckAsync(CurrentRecord);
            if (!beforeUpdateCheckResult.Success)
            {
                NotifyError(beforeUpdateCheckResult.Message);
                modalVisible = true;
                return;
            }

            CurrentRecord.UpdatedAt = DateTime.Now;
            var updateResult = await meetingService.UpdateAsync(CurrentRecord);
            if (!updateResult.Success)
            {
                NotifyError(updateResult.Message);
                modalVisible = true;
                return;
            }

            logger.LogInformation("Meeting update submitted. MeetingId={MeetingId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            NotifySuccess("修改成功");
        }

        // 主資料存好之後才上傳影音檔——新增模式下要等資料庫給 Id 才知道檔案掛在哪一筆。
        if (pendingMediaFile is not null)
        {
            var uploadSucceeded = await UploadPendingMediaAsync();
            if (!uploadSucceeded)
            {
                // 主資料已存檔，只有上傳失敗；讓使用者留在 Modal 重試，不關閉視窗。
                await ReloadAsync();
                modalVisible = true;
                return;
            }
        }

        await ReloadAsync();
        modalVisible = false;
        ResetUploadState();
    }

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        if (isUploading)
        {
            return Task.CompletedTask;
        }

        modalVisible = false;
        ResetUploadState();
        logger.LogDebug("Meeting modal cancelled.");
        return Task.CompletedTask;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        // 描述為多行輸入，Enter 保留給換行，只處理 Esc（與提示詞頁一致）。
        if (args.Key == "Escape" || args.Key == "Esc")
        {
            await OnModalCancelHandleAsync(new MouseEventArgs());
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    #endregion

    #region 影音檔上傳與轉錄

    private Task OnMediaFileSelectedAsync(InputFileChangeEventArgs args)
    {
        var file = args.File;

        if (!MeetingMediaPolicy.IsAllowedFileName(file.Name))
        {
            NotifyError($"「{file.Name}」不是支援的影音格式，允許的格式：{MeetingMediaPolicy.AllowedExtensionsText}。");
            return Task.CompletedTask;
        }

        if (file.Size > MeetingMediaPolicy.MaxUploadFileSize)
        {
            NotifyError($"「{file.Name}」超過單檔上限 {MeetingMediaPolicy.FormatFileSize(MeetingMediaPolicy.MaxUploadFileSize)}。");
            return Task.CompletedTask;
        }

        pendingMediaFile = file;
        uploadPercent = 0;
        logger.LogInformation("Meeting media file selected. FileName={FileName}, FileSize={FileSize}", file.Name, file.Size);
        return Task.CompletedTask;
    }

    private void ClearPendingMediaFile()
    {
        pendingMediaFile = null;
        uploadPercent = 0;
    }

    private async Task<bool> UploadPendingMediaAsync()
    {
        if (pendingMediaFile is null)
        {
            return true;
        }

        isUploading = true;
        uploadPercent = 0;
        StateHasChanged();

        try
        {
            await using var stream = pendingMediaFile.OpenReadStream(MeetingMediaPolicy.MaxUploadFileSize);

            var progress = new UploadProgress(percent =>
            {
                uploadPercent = percent;
                _ = InvokeAsync(StateHasChanged);
            });

            var result = await meetingService.SaveMediaAsync(
                CurrentRecord.Id,
                new MeetingMediaUploadInput
                {
                    FileName = pendingMediaFile.Name,
                    ContentType = pendingMediaFile.ContentType,
                    FileSize = pendingMediaFile.Size,
                    Content = stream,
                },
                progress);

            if (!result.Success)
            {
                NotifyError(result.Message);
                return false;
            }

            pendingMediaFile = null;
            NotifySuccess("影音檔已上傳，系統已排入背景轉錄，完成後可在清單預覽逐字稿。");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload meeting media. MeetingId={MeetingId}", CurrentRecord.Id);
            NotifyError($"影音檔上傳失敗：{ex.Message}");
            return false;
        }
        finally
        {
            isUploading = false;
            StateHasChanged();
        }
    }

    private async Task OnRetryTranscriptionAsync(MeetingAdapterModel meetingAdapterModel)
    {
        logger.LogInformation("Retry transcription requested. MeetingId={MeetingId}", meetingAdapterModel.Id);

        var result = await meetingService.RequeueTranscriptionAsync(meetingAdapterModel.Id);
        if (!result.Success)
        {
            NotifyError(result.Message);
            return;
        }

        NotifySuccess("已重新排入轉錄佇列，請稍後重新整理查看結果。");
        await ReloadAsync();
    }

    private async Task OnPreviewTranscriptAsync(MeetingAdapterModel meetingAdapterModel)
    {
        logger.LogInformation("Transcript preview requested. MeetingId={MeetingId}", meetingAdapterModel.Id);

        var content = await meetingService.ReadTranscriptAsync(meetingAdapterModel.Id);
        if (content is null)
        {
            NotifyError("找不到逐字稿檔案，請重新執行轉錄。");
            return;
        }

        transcriptModalTitle = $"逐字稿 - {meetingAdapterModel.Title}";
        transcriptContent = content;

        // 只有轉錄完成的才給編修：正在重新轉錄時存回去，只會被即將產生的新逐字稿覆蓋。
        editingTranscriptMeetingId = meetingAdapterModel.Id;
        canEditTranscript = meetingAdapterModel.TranscriptionStatus == TranscriptionStatus.Completed;

        transcriptModalVisible = true;
    }

    private async Task OnSaveTranscriptAsync()
    {
        if (!canEditTranscript || isSavingTranscript || editingTranscriptMeetingId <= 0)
        {
            return;
        }

        isSavingTranscript = true;

        try
        {
            var result = await meetingService.UpdateTranscriptAsync(editingTranscriptMeetingId, transcriptContent);
            if (!result.Success)
            {
                NotifyError(result.Message);
                return;
            }

            await messageService.SuccessAsync("逐字稿已儲存");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving transcript failed. MeetingId={MeetingId}", editingTranscriptMeetingId);
            NotifyError($"儲存逐字稿失敗：{ex.Message}");
        }
        finally
        {
            isSavingTranscript = false;
        }
    }

    private Task OnTranscriptModalCancelHandleAsync(MouseEventArgs args)
    {
        transcriptModalVisible = false;
        transcriptContent = string.Empty;
        editingTranscriptMeetingId = 0;
        canEditTranscript = false;
        return Task.CompletedTask;
    }

    private void ResetUploadState()
    {
        pendingMediaFile = null;
        isUploading = false;
        uploadPercent = 0;
    }

    #endregion

    /// <summary>
    /// 取這一列目前的即時進度；沒有進行中的工作時回傳 null（狀態欄就只顯示徽章）。
    /// 資料來自記憶體中的通知器，不查資料庫。
    /// </summary>
    private TranscriptionProgressItem? GetLiveProgress(int meetingId)
    {
        var item = progressNotifier.Find(meetingId);
        return item is { IsRunning: true } ? item : null;
    }

    private static string DescribeProgressPhase(TranscriptionProgressItem item) => item.Phase switch
    {
        TranscriptionPhase.Queued => "排隊中",
        TranscriptionPhase.Converting => "轉檔中",
        TranscriptionPhase.Transcribing => $"轉錄中（第 {item.CompletedSegments}/{item.TotalSegments} 段）",
        _ => string.Empty,
    };

    private static string GetStatusCssClass(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.Pending => "meeting-status-pending",
        TranscriptionStatus.Processing => "meeting-status-processing",
        TranscriptionStatus.Completed => "meeting-status-completed",
        TranscriptionStatus.Failed => "meeting-status-failed",
        // 取消是中性結果，沿用 none 的灰色；明寫出來以免日後有人把它當成遺漏而改成紅色。
        TranscriptionStatus.Cancelled => "meeting-status-none",
        _ => "meeting-status-none",
    };

    private void NotifySuccess(string description)
    {
        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private void NotifyError(string description)
    {
        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight,
            Duration = 8
        });
    }

    /// <summary>
    /// 上傳進度轉接。刻意不用 <see cref="Progress{T}"/>——它會把回呼排到擷取到的
    /// SynchronizationContext，在 Blazor Server 上進度更新會被延後到上傳結束才一次湧現。
    /// </summary>
    private sealed class UploadProgress : IProgress<int>
    {
        private readonly Action<int> onReport;

        public UploadProgress(Action<int> onReport) => this.onReport = onReport;

        public void Report(int value) => onReport(value);
    }

    public void Dispose() => progressNotifier.Changed -= OnTranscriptionProgressChanged;
}
