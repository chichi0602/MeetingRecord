using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Services;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Views.Meetings;

public partial class MeetingViewView : IDisposable
{
    private readonly ILogger<MeetingViewView> logger;
    private readonly MeetingService meetingService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private readonly ProjectService projectService;
    private readonly PromptTemplateService promptTemplateService;
    private readonly IMeetingDraftProgressNotifier draftProgressNotifier;
    private readonly FileDownloadInterop fileDownloadInterop;
    private readonly IPdfRenderer pdfRenderer;
    private ITable? table;

    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    /// <summary>已因「轉錄結束」重新載入過的會議 Id，避免同一筆重複觸發重載。</summary>
    private readonly HashSet<int> reloadedFinishedMeetingIds = [];

    /// <summary>
    /// 已因「會議紀錄生成結束」重新載入過的會議 Id。
    ///
    /// ⚠️ 必須和轉錄那一份分開。共用一份的話，一筆會議轉錄完成後 Id 就已經在集合裡，
    /// 之後同一筆草稿生成完成時 Add 會回 false、重新載入被靜默跳過，
    /// 狀態欄會一直停在「生成中」直到使用者手動重新整理。
    /// </summary>
    private readonly HashSet<int> reloadedFinishedDraftMeetingIds = [];

    private List<MeetingAdapterModel> meetingAdapterModels = [];

    private string modalTitle = "會議紀錄維護";
    private bool modalVisible;
    private MeetingAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IBrowserFile? pendingMediaFile;
    private bool isUploading;

    /// <summary>正在重新排入轉錄的會議 Id。CrudActionButton 沒有 Loading 參數，只能靠 Disabled 擋重複點擊。</summary>
    private int? requeueingMeetingId;

    /// <summary>
    /// 轉錄費用說明。刻意不講「幾次 API 呼叫」：段數要等 FFmpeg 切完才知道，畫面上只有檔案大小，
    /// 而位元率在語音備忘錄與含影軌的 MP4 之間差十倍以上，換算出來的數字是假精確。
    /// 分鐘數從 SegmentSeconds 算，不要寫死，否則改常數時文案會偷偷過期。
    /// </summary>
    private static readonly string TranscriptionCostNotice =
        $"轉錄會呼叫 Azure OpenAI 語音服務並產生費用：音檔每 {FfmpegMediaConverter.SegmentSeconds / 60} 分鐘切成一段、逐段送出，音檔越長費用越高。";
    private int uploadPercent;

    private bool transcriptModalVisible;
    private string transcriptModalTitle = "逐字稿";
    private string transcriptContent = string.Empty;

    /// <summary>目前開著的逐字稿屬於哪一筆會議；儲存時要用。</summary>
    private int editingTranscriptMeetingId;
    private bool canEditTranscript;
    private bool isSavingTranscript;

    #region AI 會議紀錄

    /// <summary>
    /// 產生會議紀錄的費用說明。與專案頁各留一份，不抽共用常數——
    /// 兩處的上下文不同，抽出來只會多一個為了單一用途存在的檔案。
    /// </summary>
    private const string DraftCostNotice =
        "產生會議紀錄會呼叫 Azure OpenAI 文字生成服務並產生費用：逐字稿較長時會先分段摘要再合併，段數越多費用越高。";

    private const string PdfContentType = "application/pdf";

    /// <summary>專案與提示詞清單只在開啟對話框時撈，不放進 ReloadAsync——那支會被兩個進度通知器頻繁呼叫。</summary>
    private List<ProjectAdapterModel> projects = [];
    private List<PromptTemplateAdapterModel> promptTemplates = [];

    private bool draftRequestVisible;
    private int draftRequestMeetingId;
    private string draftRequestMeetingTitle = string.Empty;
    private int draftRequestPromptTemplateId;
    private int draftRequestProjectId;
    private List<string> draftRequestAttendees = [];
    private bool draftRequestHasDraft;

    /// <summary>已歸屬的逐字稿重新產生時不得改歸屬，下拉要預選並鎖住。</summary>
    private bool draftRequestLockedProject;

    /// <summary>
    /// 對話框開啟次數。用來當 Select 的 @key。
    ///
    /// ⚠️ 不能用資料的 Id 當 key——同一筆會議關掉再開，Id 不變、元件實例不會重建，
    /// 上一次的標籤會留在畫面上（Modal 只靠 @bind-Visible 開關，不是條件渲染）。
    /// </summary>
    private int draftRequestOpenCount;
    private bool isGenerating;

    private bool attachVisible;
    private int attachMeetingId;
    private string attachMeetingTitle = string.Empty;
    private int attachProjectId;
    private int attachOpenCount;
    private bool isAttaching;

    private bool draftViewModalVisible;
    private bool draftEditModalVisible;
    private int draftMeetingId;
    private string draftModalTitle = "會議紀錄";
    private string? draftContent;
    private bool isSavingDraft;

    /// <summary>正在匯出 PDF 的會議 Id。PDF 由無頭瀏覽器列印，會啟動外部程序，要擋重複點擊。</summary>
    private int? exportingMeetingId;

    private bool aiChatVisible;
    private int aiChatTargetId;
    private string aiChatTitle = "AI 問答";
    private string aiChatTargetName = string.Empty;

    private bool todoExtractionVisible;
    private int todoExtractionMeetingId;
    private int todoExtractionProjectId;
    private string todoExtractionMeetingTitle = string.Empty;

    /// <summary>目前選到的專案有沒有與會人員名冊；名冊是空的時候選擇器停用但不隱藏。</summary>
    private List<string> DraftRequestParticipants
        => projects.FirstOrDefault(x => x.Id == draftRequestProjectId)?.Participants ?? [];

    private string DraftRequestAttendeePlaceholder => draftRequestProjectId <= 0
        ? "請先選擇專案"
        : DraftRequestParticipants.Count == 0 ? "此專案尚未設定與會人員" : "勾選本次到場的人";

    #endregion

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
        ITranscriptionProgressNotifier progressNotifier,
        ProjectService projectService,
        PromptTemplateService promptTemplateService,
        IMeetingDraftProgressNotifier draftProgressNotifier,
        FileDownloadInterop fileDownloadInterop,
        IPdfRenderer pdfRenderer)
    {
        this.logger = logger;
        this.meetingService = meetingService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
        this.progressNotifier = progressNotifier;
        this.projectService = projectService;
        this.promptTemplateService = promptTemplateService;
        this.draftProgressNotifier = draftProgressNotifier;
        this.fileDownloadInterop = fileDownloadInterop;
        this.pdfRenderer = pdfRenderer;
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
        draftProgressNotifier.Changed += OnDraftProgressChanged;

        await ReloadAsync();
    }

    /// <summary>
    /// 會議紀錄生成進度變動時更新「會議紀錄」欄。
    /// 立場與轉錄那支相同：進行中只重繪，結束才重新查庫。
    /// </summary>
    private void OnDraftProgressChanged()
        => _ = InvokeAsync(async () =>
        {
            var finishedIds = draftProgressNotifier
                .GetSnapshot()
                .Where(x => x.Phase is MeetingDraftPhase.Completed or MeetingDraftPhase.Failed)
                .Select(x => x.MeetingId)
                .ToList();

            // 用 Count 而非 Any——Any 會短路，漏掉同時完成的其他筆。
            var newlyFinishedCount = finishedIds.Count(reloadedFinishedDraftMeetingIds.Add);

            if (newlyFinishedCount > 0)
            {
                await ReloadAsync();
            }
            else
            {
                StateHasChanged();
            }
        });

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


        // 費用確認必須在主資料存檔「之前」：這顆確定鈕同時做存檔與上傳，
        // 放在存檔之後按取消，會留下一筆存好卻沒有音檔的紀錄，而且「修改成功」已經跳過了。
        // 也不能放在驗證之前——不該為了一張即將驗證失敗的表單問使用者要不要花錢。
        if (pendingMediaFile is not null)
        {
            var replacingMedia = CurrentRecord.HasMedia;
            var fileDescription = $"「{pendingMediaFile.Name}」（{MeetingMediaPolicy.FormatFileSize(pendingMediaFile.Size)}）";

            var uploadConfirmOptions = new ConfirmOptions
            {
                Title = "確認上傳並開始轉錄（會產生費用）",
                Content = replacingMedia
                    ? $"{fileDescription}會取代現有的「{CurrentRecord.MediaOriginalFileName}」，"
                      + $"現有音檔與逐字稿會被刪除且無法復原。儲存後會立刻自動排入背景轉錄：{TranscriptionCostNotice}確定要繼續嗎？"
                    : $"{fileDescription}儲存後會立刻自動排入背景轉錄：{TranscriptionCostNotice}確定要繼續嗎？",
                OkText = replacingMedia ? "取代並開始轉錄" : "儲存並開始轉錄",
                CancelText = "取消",
                MaskClosable = false
            };

            if (replacingMedia)
            {
                uploadConfirmOptions.OkButtonProps = new ButtonProps { Danger = true };
            }

            var uploadConfirmed = await modalService.ConfirmAsync(uploadConfirmOptions);
            if (!uploadConfirmed)
            {
                logger.LogDebug("Media upload cancelled by user at cost confirmation. MeetingId={MeetingId}", CurrentRecord.Id);
                // 這是 Modal 的 OnOk，AntDesign 會自己把視窗關掉；不重新開啟的話
                // 使用者填的整張表單會消失（與驗證失敗分支同一個處理）。
                modalVisible = true;
                return;
            }
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

            // ⚠️ 這一筆已經存進資料庫、CurrentRecord.Id 也已經被填上了，所以不能再算是「新增模式」。
            //    下面的影音檔上傳若失敗，使用者會留在這個 Modal 重試；此時若仍是新增模式，
            //    再按一次「確定」會拿著同一個 Id 再 INSERT 一次，得到
            //    「UNIQUE constraint failed: Meeting.Id」——整個視窗就卡死了，只能取消重來。
            isNewRecordMode = false;
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
        // 0.4.77 起本頁也支援 Enter 送出。先前刻意排除，理由是「描述為多行輸入，
        // Enter 保留給換行」——那個理由在 FormKeyboardHelper 之後不成立了：
        // Shift+Enter 會落回瀏覽器原生的換行，而組字中的 Enter 被 IsComposing 擋掉。
        if (FormKeyboardHelper.IsSubmit(args))
        {
            // AntDesign Input 預設 change/blur 才回寫，不等就會拿到舊值。
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

    /// <summary>
    /// 底下那個拖拉區。用完檔案之後要呼叫 Reset()，「移除 → 再拖同一個檔案」才會有反應。
    /// </summary>
    private FileDropZone? mediaDropZone;

    private void ClearPendingMediaFile()
    {
        pendingMediaFile = null;
        uploadPercent = 0;

        // ⚠️ 只能在這裡（確定不再需要那個 IBrowserFile 之後）重建 input。
        //    在「剛選完檔案」時重建，會把檔案自己弄丟——見 FileDropZone 的註解。
        mediaDropZone?.Reset();
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

            // 檔案已經讀完寫進儲存區了，這時才可以重建 input（見 FileDropZone.Reset）。
            pendingMediaFile = null;
            mediaDropZone?.Reset();
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
        if (requeueingMeetingId is not null)
        {
            return;
        }

        logger.LogInformation("Retry transcription requested. MeetingId={MeetingId}", meetingAdapterModel.Id);

        // Danger 跟「覆蓋」走、不跟「花錢」走（見 開發慣例與限制速查 的 UI 慣例）：
        // 只有已完成的那一支會刪掉現有逐字稿，才算破壞性動作。
        var willOverwrite = meetingAdapterModel.TranscriptionStatus == TranscriptionStatus.Completed;

        var situation = meetingAdapterModel.TranscriptionStatus switch
        {
            TranscriptionStatus.Completed
                => $"「{meetingAdapterModel.Title}」已經有逐字稿了，重新轉錄會覆蓋現有逐字稿，舊檔案會被刪除且無法復原。",
            TranscriptionStatus.Failed
                => $"「{meetingAdapterModel.Title}」上次轉錄失敗，這會重新跑一次完整的轉錄。",
            TranscriptionStatus.Cancelled
                => $"「{meetingAdapterModel.Title}」上次轉錄已取消，這會從頭重跑，不會接續上次的進度。",
            _ => $"這會為「{meetingAdapterModel.Title}」執行一次完整的轉錄。",
        };

        var confirmOptions = new ConfirmOptions
        {
            Title = willOverwrite ? "確認重新轉錄（會產生費用）" : "確認執行轉錄（會產生費用）",
            Content = $"{situation}{TranscriptionCostNotice}確定要繼續嗎？",
            OkText = willOverwrite ? "覆蓋並重新轉錄" : "開始轉錄",
            CancelText = "取消",
            MaskClosable = false
        };

        if (willOverwrite)
        {
            confirmOptions.OkButtonProps = new ButtonProps { Danger = true };
        }

        var ok = await modalService.ConfirmAsync(confirmOptions);
        if (!ok)
        {
            logger.LogDebug("Retry transcription cancelled by user. MeetingId={MeetingId}", meetingAdapterModel.Id);
            return;
        }

        requeueingMeetingId = meetingAdapterModel.Id;
        try
        {
            var result = await meetingService.RequeueTranscriptionAsync(meetingAdapterModel.Id);
            if (!result.Success)
            {
                NotifyError(result.Message);
                return;
            }

            NotifySuccess("已重新排入轉錄佇列，可在右下角的進度面板看到進度。");
            await ReloadAsync();
        }
        finally
        {
            requeueingMeetingId = null;
        }
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
        mediaDropZone?.Reset();
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

    #region AI 會議紀錄

    /// <summary>開啟「AI 轉會議紀錄」對話框。專案與提示詞清單在這裡才撈，確保是最新的。</summary>
    private async Task OnOpenDraftRequestAsync(MeetingAdapterModel meeting)
    {
        projects = await projectService.GetSelectableAsync();
        promptTemplates = await promptTemplateService.GetEnabledSelectableAsync();

        draftRequestMeetingId = meeting.Id;
        draftRequestMeetingTitle = meeting.Title;
        draftRequestHasDraft = meeting.HasDraft;
        draftRequestPromptTemplateId = promptTemplates.Count == 1 ? promptTemplates[0].Id : 0;

        // 已歸屬的逐字稿重新產生時不得改歸屬（服務層也會擋），下拉直接預選並鎖住，
        // 使用者才不會看到一個已歸屬的會議顯示「不指定專案」。
        draftRequestLockedProject = meeting.ProjectId is not null;
        draftRequestProjectId = meeting.ProjectId ?? 0;
        draftRequestAttendees = [];
        draftRequestOpenCount++;
        draftRequestVisible = true;
    }

    /// <summary>名冊是專案私有的，換專案一定要清掉已勾選的與會者。</summary>
    private void OnDraftRequestProjectChanged() => draftRequestAttendees = [];

    private void OnDraftRequestAttendeesChanged(IEnumerable<string>? values)
        => draftRequestAttendees = values?.ToList() ?? [];

    /// <summary>
    /// 「AI 轉會議紀錄」對話框的 Enter 送出。
    /// ⚠️ 走的是與按鈕完全相同的 <see cref="OnDraftRequestOkAsync"/>，所以那道費用二次確認
    /// 照樣會跳——Enter 只是取代滑鼠點「開始產生」，不會略過任何一關。
    /// </summary>
    private async Task OnDraftRequestKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnDraftRequestOkAsync(new MouseEventArgs());
        }
    }

    private async Task OnDraftRequestOkAsync(MouseEventArgs args)
    {
        if (isGenerating)
        {
            return;
        }

        if (draftRequestPromptTemplateId <= 0)
        {
            NotifyError("請選擇提示詞。");
            // AntDesign 的 Modal 在 OnOk 之後會自己關窗，不推回去整張表單就消失了。
            draftRequestVisible = true;
            return;
        }

        // 首次生成也要確認：確認的理由是「會花錢」。覆蓋只是讓它「同時」變成破壞性動作，
        // 所以 Danger 掛在 willOverwrite 上，而不是掛在「有費用」上。
        var willOverwrite = draftRequestHasDraft;
        var situation = willOverwrite
            ? "這份逐字稿已經產生過會議紀錄，重新產生會覆蓋既有內容（包含人工編修過的部分），且無法復原。"
            : "將以選定的提示詞為這份逐字稿產生會議紀錄。";

        var confirmOptions = new ConfirmOptions
        {
            Title = willOverwrite ? "確認重新產生（會產生費用）" : "確認產生會議紀錄（會產生費用）",
            Content = $"{situation}{DraftCostNotice}確定要繼續嗎？",
            OkText = willOverwrite ? "覆蓋並重新產生" : "開始產生",
            CancelText = "取消",
            MaskClosable = false
        };

        if (willOverwrite)
        {
            confirmOptions.OkButtonProps = new ButtonProps { Danger = true };
        }

        if (!await modalService.ConfirmAsync(confirmOptions))
        {
            logger.LogDebug("Draft generation cancelled by user. MeetingId={MeetingId}", draftRequestMeetingId);
            draftRequestVisible = true;
            return;
        }

        isGenerating = true;
        try
        {
            logger.LogInformation(
                "Draft generation requested from meeting view. MeetingId={MeetingId}, ProjectId={ProjectId}, PromptTemplateId={PromptTemplateId}",
                draftRequestMeetingId,
                draftRequestProjectId,
                draftRequestPromptTemplateId);

            var result = await meetingService.RequestDraftAsync(
                draftRequestMeetingId,
                draftRequestProjectId > 0 ? draftRequestProjectId : null,
                draftRequestPromptTemplateId,
                draftRequestAttendees);

            if (!result.Success)
            {
                NotifyError(result.Message);
                draftRequestVisible = true;
                return;
            }

            draftRequestVisible = false;
            NotifySuccess("已排入生成佇列，可在右下角看到進度，完成後清單會自動更新。");
            await ReloadAsync();
        }
        finally
        {
            isGenerating = false;
        }
    }

    private Task OnDraftRequestCancelAsync(MouseEventArgs args)
    {
        draftRequestVisible = false;
        return Task.CompletedTask;
    }

    /// <summary>開啟「歸屬到專案」對話框。</summary>
    private async Task OnOpenAttachAsync(MeetingAdapterModel meeting)
    {
        projects = await projectService.GetSelectableAsync();

        attachMeetingId = meeting.Id;
        attachMeetingTitle = meeting.Title;
        attachProjectId = 0;
        attachOpenCount++;
        attachVisible = true;
    }

    /// <summary>「歸屬到專案」對話框的 Enter 送出（同樣會經過那道二次確認）。</summary>
    private async Task OnAttachKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnAttachOkAsync(new MouseEventArgs());
        }
    }

    private async Task OnAttachOkAsync(MouseEventArgs args)
    {
        if (isAttaching)
        {
            return;
        }

        if (attachProjectId <= 0)
        {
            NotifyError("請選擇要歸屬的專案。");
            attachVisible = true;
            return;
        }

        var projectTitle = projects.FirstOrDefault(x => x.Id == attachProjectId)?.Title ?? string.Empty;

        // 刻意不寫「會產生費用」——這支的重點就是不重跑。
        // 但要講清楚已產生的內容不會因此套上該專案的常用名詞，否則使用者會有錯誤期待。
        var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認歸屬到專案",
            Content = $"將把「{attachMeetingTitle}」歸屬到「{projectTitle}」。"
                + "已產生的會議紀錄內容不會變動，也不會重新生成或產生任何費用"
                + "（包含尚未套用該專案常用名詞的部分；若要套用請歸屬後重新產生）。"
                + "歸屬後即可抽出待辦事項。確定要歸屬嗎？",
            OkText = "歸屬",
            CancelText = "取消",
            MaskClosable = false
        });

        if (!confirmed)
        {
            attachVisible = true;
            return;
        }

        isAttaching = true;
        try
        {
            var result = await meetingService.AttachToProjectAsync(attachMeetingId, attachProjectId);
            if (!result.Success)
            {
                NotifyError(result.Message);
                attachVisible = true;
                return;
            }

            attachVisible = false;
            NotifySuccess($"已歸屬到「{projectTitle}」。");
            await ReloadAsync();
        }
        finally
        {
            isAttaching = false;
        }
    }

    private Task OnAttachCancelAsync(MouseEventArgs args)
    {
        attachVisible = false;
        return Task.CompletedTask;
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
        isSavingDraft = true;
        try
        {
            var result = await meetingService.UpdateDraftAsync(draftMeetingId, draftContent);
            if (!result.Success)
            {
                NotifyError(result.Message);
                draftEditModalVisible = true;
                return;
            }

            draftEditModalVisible = false;
            NotifySuccess("會議紀錄已儲存。");
            await ReloadAsync();
        }
        finally
        {
            isSavingDraft = false;
        }
    }

    private Task OnDraftEditModalCancelAsync(MouseEventArgs args)
    {
        draftEditModalVisible = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把會議紀錄匯出成 PDF 並直接推給瀏覽器下載（檔案不落地）。
    /// 未歸屬專案時表頭的「專案」欄會是「未指定」，不影響匯出。
    /// </summary>
    private async Task OnExportDraftAsync(MeetingAdapterModel meeting)
    {
        if (!meeting.HasDraft || exportingMeetingId is not null)
        {
            return;
        }

        exportingMeetingId = meeting.Id;
        StateHasChanged();

        try
        {
            var fileName = MeetingDocumentExporter.BuildFileName(meeting);
            var html = MeetingDocumentExporter.BuildHtml(meeting);
            var pdf = await pdfRenderer.RenderAsync(html);

            await fileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);

            logger.LogInformation(
                "Meeting draft exported as pdf from meeting view. MeetingId={MeetingId}, FileName={FileName}, Bytes={Bytes}",
                meeting.Id,
                fileName,
                pdf.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export meeting draft. MeetingId={MeetingId}", meeting.Id);
            NotifyError($"匯出 PDF 失敗：{ex.Message}");
        }
        finally
        {
            exportingMeetingId = null;
            StateHasChanged();
        }
    }

    /// <summary>就單一會議提問：讀該會議的會議紀錄與逐字稿，與專案無關。</summary>
    private void OpenMeetingChat(MeetingAdapterModel meeting)
    {
        aiChatTargetId = meeting.Id;
        aiChatTitle = $"AI 問答 - {meeting.Title}";
        aiChatTargetName = meeting.Title;
        aiChatVisible = true;
    }

    /// <summary>
    /// 從這場會議的會議紀錄抽出待辦。
    ///
    /// ⚠️ 未歸屬時按鈕仍然保持啟用，改在這裡擋——CrudActionButton 是 Tooltip 包 Button，
    /// 而停用的 button 不觸發滑鼠事件，Tooltip 永遠不會出現，提示等於不存在。
    /// </summary>
    private async Task OnExtractTodosAsync(MeetingAdapterModel meeting)
    {
        if (meeting.ProjectId is null)
        {
            await messageService.WarningAsync("待辦事項一定隸屬於某個專案，請先用「歸屬到專案」把這筆會議紀錄歸檔後再抽出待辦。");
            return;
        }

        // 抽出待辦是付費動作：TodoExtractionModal 一開啟就呼叫 ExtractAsync，
        // 視窗顯示出來時 API 已經打出去了，所以確認一定要擋在開視窗之前。
        // 文案與專案項目頁逐字相同——同一個動作在兩頁講不同的話會讓人以為行為不同。
        // 不套 Danger：抽出來的只是候選，勾選並儲存後才真的建立待辦。
        var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認抽出待辦（會產生費用）",
            Content = $"將把「{meeting.Title}」的會議紀錄全文送給 AI 分析待辦事項，"
                    + "這會呼叫 Azure OpenAI 文字生成服務並產生費用（每次抽取固定一次呼叫）。"
                    + "抽出的項目要勾選並儲存才會真的建立待辦；關閉視窗後再開啟會重新抽一次、再計費一次。確定要繼續嗎？",
            OkText = "開始抽取",
            CancelText = "取消",
            MaskClosable = false
        });

        if (!confirmed)
        {
            logger.LogDebug("Todo extraction cancelled by user. MeetingId={MeetingId}", meeting.Id);
            return;
        }

        todoExtractionMeetingId = meeting.Id;
        todoExtractionProjectId = meeting.ProjectId.Value;
        todoExtractionMeetingTitle = meeting.Title;
        todoExtractionVisible = true;
    }

    private MeetingDraftProgressItem? GetLiveDraftProgress(int meetingId)
    {
        var item = draftProgressNotifier.Find(meetingId);
        return item is { IsRunning: true } ? item : null;
    }

    private static string GetDraftStatusCssClass(DraftStatus status) => status switch
    {
        DraftStatus.Pending => "meeting-status-pending",
        DraftStatus.Processing => "meeting-status-processing",
        DraftStatus.Completed => "meeting-status-completed",
        DraftStatus.Failed => "meeting-status-failed",
        // 取消是中性結果，沿用 none 的灰色；明寫出來以免日後有人把它當成遺漏而改成紅色。
        DraftStatus.Cancelled => "meeting-status-none",
        _ => "meeting-status-none",
    };

    #endregion

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

    public void Dispose()
    {
        progressNotifier.Changed -= OnTranscriptionProgressChanged;
        draftProgressNotifier.Changed -= OnDraftProgressChanged;
    }
}
