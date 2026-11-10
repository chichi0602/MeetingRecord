using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Client.Uploads;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.Meetings;

/// <summary>
/// 會議紀錄（1.1.23 搬到 WASM，原 Server 的 <c>MeetingViewView</c>）：畫面、文字、確認框、通知全部照舊。
/// <list type="bullet">
/// <item>資料走 <see cref="MeetingApi"/>（規則都在伺服器的 MeetingService）；按鈕依 <see cref="PermissionView"/>，
/// 能不能刪、是不是上傳者由 API 依呼叫者算好（<see cref="MeetingDto.CanDelete"/>、<see cref="MeetingDto.IsUploader"/>）。</item>
/// <item>上傳交給整個前端共用的 <see cref="MeetingUploadManager"/>：換到其他 WASM 頁照樣繼續，結果通知也由它跳。</item>
/// <item>轉錄、生成進度讀 <c>api/v1/jobs</c>，有推播（<see cref="IJobProgressSource"/>）就重查；工作結束才重新載入清單。</item>
/// </list>
/// </summary>
public partial class MeetingPage : IDisposable
{
    [Inject] private ILogger<MeetingPage> logger { get; set; } = default!;

    [Inject] private MeetingApi MeetingApi { get; set; } = default!;

    [Inject] private JobApi JobApi { get; set; } = default!;

    [Inject] private IJobProgressSource JobProgressSource { get; set; } = default!;

    [Inject] private MeetingUploadManager UploadManager { get; set; } = default!;

    [Inject] private PermissionView PermissionView { get; set; } = default!;

    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;

    [Inject] private ModalService modalService { get; set; } = default!;

    [Inject] private MessageService messageService { get; set; } = default!;

    [Inject] private NotificationService notificationService { get; set; } = default!;

    [Inject] private ClipboardInterop clipboardInterop { get; set; } = default!;

    [Inject] private FileDownloadInterop fileDownloadInterop { get; set; } = default!;

    /// <summary>啟用中的分類名稱（表單與篩選共用）。</summary>
    private List<string> availableCategories = [];

    /// <summary>清單的分類篩選；空＝不篩選。</summary>
    private List<string> selectedCategoryFilters = [];
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

    /// <summary>目前看得到的背景工作（依會議編號）；只有進行中的會顯示在列上。</summary>
    private Dictionary<int, BackgroundJobDto> transcriptionJobs = [];
    private Dictionary<int, BackgroundJobDto> draftJobs = [];

    private IDisposable? jobSubscription;

    private List<MeetingDto> meetings = [];

    private string modalTitle = "會議紀錄維護";
    private bool modalVisible;
    private MeetingDto CurrentRecord = NewRecord();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IBrowserFile? pendingMediaFile;

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>
    /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard</c> 與表單上的 <c>@onkeydown</c>，
    /// 兩者都會呼叫 <see cref="OnModalCancelHandleAsync"/>。少了這道旗標會疊出兩個確認視窗。
    /// </summary>
    private bool isDiscardConfirming;

    /// <summary>正在重新排入轉錄的會議 Id。CrudActionButton 沒有 Loading 參數，只能靠 Disabled 擋重複點擊。</summary>
    private int? requeueingMeetingId;

    /// <summary>
    /// 轉錄費用說明。刻意不講「幾次 API 呼叫」：段數要等 FFmpeg 切完才知道，畫面上只有檔案大小，
    /// 而位元率在語音備忘錄與含影軌的 MP4 之間差十倍以上，換算出來的數字是假精確。
    /// 分鐘數從分段秒數算，不要寫死，否則改常數時文案會偷偷過期。
    /// </summary>
    private static readonly string TranscriptionCostNotice =
        $"轉錄會呼叫 Azure OpenAI 語音服務並產生費用：音檔每 {MeetingMediaPolicy.TranscriptionSegmentSeconds / 60} 分鐘切成一段、逐段送出，音檔越長費用越高。";

    #region AI 會議紀錄

    /// <summary>
    /// 產生會議紀錄的費用說明。與專案頁各留一份，不抽共用常數——
    /// 兩處的上下文不同，抽出來只會多一個為了單一用途存在的檔案。
    /// </summary>
    private const string DraftCostNotice =
        "產生會議紀錄會呼叫 Azure OpenAI 文字生成服務並產生費用：逐字稿較長時會先分段摘要再合併，段數越多費用越高。";

    /// <summary>專案與提示詞清單只在開啟對話框時撈，不放進 ReloadAsync——那支會被背景工作推播頻繁呼叫。</summary>
    private List<MeetingProjectOptionDto> projects = [];
    private List<MeetingPromptOptionDto> promptTemplates = [];

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
    /// 上一次的標籤會留在畫面上（Modal 只靠 Visible 開關，不是條件渲染）。
    /// </summary>
    private int draftRequestOpenCount;
    private bool isGenerating;

    private bool attachVisible;
    private int attachMeetingId;
    private string attachMeetingTitle = string.Empty;
    private int attachProjectId;
    private int attachOpenCount;
    private bool isAttaching;

    // 檢視與編修共用同一個 MarkdownEditorModal，所以只有一組狀態；形狀由 draftCanEdit 決定。
    private bool draftModalVisible;
    private bool draftCanEdit;
    private bool draftCanExport;
    private int draftMeetingId;
    private string draftModalTitle = "會議紀錄";
    private string? draftContent;
    private string? draftNotice;
    private bool draftCanSwitchToEdit;
    private string? draftTranscript;
    private bool draftCanCopyTranscript;
    private bool isSavingDraft;

    /// <summary>目前視窗對應的會議。</summary>
    private MeetingDto? draftMeeting;

    private bool draftCanGenerate;

    /// <summary>正在匯出 PDF 的會議 Id。PDF 由伺服器的無頭瀏覽器列印，要擋重複點擊。</summary>
    private int? exportingMeetingId;

    private bool aiChatVisible;
    private int aiChatTargetId;
    private string aiChatTitle = "AI 問答";

    /// <summary>目前選到的專案有沒有與會人員名冊；名冊是空的時候選擇器停用但不隱藏。</summary>
    private List<string> DraftRequestParticipants
        => projects.FirstOrDefault(x => x.Id == draftRequestProjectId)?.Participants ?? [];

    private string DraftRequestAttendeePlaceholder => draftRequestProjectId <= 0
        ? "請先選擇專案"
        : DraftRequestParticipants.Count == 0 ? "此專案尚未設定與會人員" : "勾選本次到場的人";

    #endregion

    private bool CanCreate => PermissionView.Can(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create);

    private bool CanEdit => PermissionView.Can(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit);

    private bool CanExport => PermissionView.Can(MagicObjectHelper.角色_會議紀錄, PermissionActions.Export);

    /// <summary>新增時一定可以選檔；修改別人上傳的會議不能換檔（1.0.47）。</summary>
    private bool CanChangeMedia => isNewRecordMode || CurrentRecord.IsUploader;

    private static bool IsUnassigned(MeetingDto meeting) => meeting.ProjectId is null;

    private static string ProjectTitleText(MeetingDto meeting) => meeting.ProjectTitle ?? "— 未歸屬";

    private static MeetingDto NewRecord() => new() { Id = 0, CategoryNames = [] };

    /// <summary>DTO 的 Id 是 <c>int?</c>（新增時沒有）；清單上的一定有值。</summary>
    private static int IdOf(MeetingDto meeting) => meeting.Id ?? 0;

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing meeting management page.");
        await PermissionView.LoadAsync();
        if (!PermissionView.CanAccessPage(MagicObjectHelper.角色_會議紀錄))
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/meetings");
            logger.LogWarning("Meeting page denied because current user has not this role permission.");
            return;
        }

        UploadManager.Changed += OnUploadsChanged;
        jobSubscription = JobProgressSource.Subscribe(OnJobsChangedAsync);

        availableCategories = (await MeetingApi.GetEnabledCategoriesAsync()).Data ?? [];
        await RefreshJobsAsync();
        await ReloadAsync();
    }

    /// <summary>
    /// 背景工作（轉錄含上傳、會議紀錄生成）有變動：重查進度。進行中只重繪（百分比來自進度清單，不必查庫）；
    /// 工作結束才重新載入，讓狀態欄從「處理中」翻成資料庫裡的「已完成／失敗」。
    /// </summary>
    private Task OnJobsChangedAsync() => InvokeAsync(async () =>
    {
        if (disposed)
        {
            return;
        }

        var newlyFinished = await RefreshJobsAsync();

        // 本頁正在存檔時只重繪：那條流程最後自己會重載（0.4.117）。這裡不記「已重載過」，之後的事件會補上。
        if (newlyFinished && !isSaving)
        {
            await ReloadAsync();
        }
        else
        {
            StateHasChanged();
        }
    });

    /// <summary>上傳開始或結束（結果通知已由 <see cref="MeetingUploadManager"/> 跳過）：重新載入，「影音檔」欄與狀態欄才會換成新值。</summary>
    private void OnUploadsChanged() => _ = InvokeAsync(async () =>
    {
        if (!disposed)
        {
            await ReloadAsync();
        }
    });

    /// <summary>
    /// 重查背景工作，回傳有沒有「這一頁還沒為它重載過」的工作剛結束。
    /// 已完成的項目會留在清單裡直到使用者關閉，所以要記下已處理過的 Id，否則之後每一次推播都會再重新載入一次。
    /// </summary>
    private async Task<bool> RefreshJobsAsync()
    {
        var jobs = await JobProgressSource.GetJobsAsync();

        var transcription = jobs.Where(x => x.Kind == BackgroundJobKinds.Transcription).ToList();
        var draft = jobs.Where(x => x.Kind == BackgroundJobKinds.Draft).ToList();
        transcriptionJobs = transcription.GroupBy(x => x.MeetingId).ToDictionary(g => g.Key, g => g.First());
        draftJobs = draft.GroupBy(x => x.MeetingId).ToDictionary(g => g.Key, g => g.First());

        // 重新上傳或重新轉錄的會議又回到進行中了，要從「已重載過」移除，這一趟結束時才會再重載（0.4.117）。
        foreach (var running in transcription.Where(x => x.IsRunning))
        {
            reloadedFinishedMeetingIds.Remove(running.MeetingId);
        }

        // HashSet.Add 回傳 true 代表這一筆是新完成的。用 Count 而非 Any，確保整個清單都被走過（Any 會短路，漏掉同時完成的其他筆）。
        var newlyFinishedTranscriptions = transcription
            .Where(x => x.IsCompleted || x.IsFailed)
            .Select(x => x.MeetingId)
            .Count(reloadedFinishedMeetingIds.Add);
        var newlyFinishedDrafts = draft
            .Where(x => x.IsCompleted || x.IsFailed)
            .Select(x => x.MeetingId)
            .Count(reloadedFinishedDraftMeetingIds.Add);

        return newlyFinishedTranscriptions + newlyFinishedDrafts > 0;
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading meetings. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        // 只有真的選了方向才送排序欄位（1.1.19 待辦頁踩過）：送了欄位卻沒方向，伺服器就不會套用預設排序。
        bool? descending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : null;
        var result = await MeetingApi.ListAsync(new MeetingListRequestDto
        {
            Keyword = searchText,
            CategoryFilters = selectedCategoryFilters.ToList(),
            SortBy = descending is null ? null : sortField,
            SortDescending = descending,
            PageIndex = _pageIndex,
            PageSize = _pageSize,
        });

        if (!result.Success || result.Data is not { } page)
        {
            NotifyError($"讀取會議紀錄失敗：{MeetingErrorText(result)}");
            return;
        }

        meetings = page.Items.ToList();
        _total = page.TotalCount;

        // 1.0.53：背景工作會觸發重新載入，勾選不能因此消失——依 Id 對回新的列，已不能刪的就拿掉。
        // 換頁、搜尋、篩選由各自的處理先清掉勾選。
        var selectedIds = selectedRows.Select(x => x.Id).ToHashSet();
        selectedRows = meetings.Where(x => selectedIds.Contains(x.Id) && CanSelect(x)).ToList();

        // 頁碼可能落在最後一頁之後（例如停在第 2 頁時把該頁唯一一筆刪掉），夾回最後一頁重載一次；
        // Math.Max(1, ...) 是因為 AntDesign 在 PageIndex < 1 時不觸發 OnChange。
        if (meetings.Count == 0 && _total > 0 && _pageIndex > 1)
        {
            _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            logger.LogDebug("Meeting page index clamped to the last page. PageIndex={PageIndex}", _pageIndex);
            await ReloadAsync();
            return;
        }

        logger.LogInformation("Meeting list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    #region 過濾、排序與分頁

    private async Task OnTableChange(QueryModel<MeetingDto> args)
    {
        _pageIndex = args.PageIndex;
        // 明確賦值，不賭 @bind-PageSize 與 AntDesign 內部 callback 的先後順序（同待辦頁）。
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

        logger.LogDebug("Meeting table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);

        // 全選只算這一頁（1.0.53）：換頁、排序就清掉勾選。
        selectedRows = [];
        await ReloadAsync();
    }

    private static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
        => sortModels.FirstOrDefault(model => model.SortDirection == SortDirection.Ascending || model.SortDirection == SortDirection.Descending)
            ?? sortModels.Last();

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

    private async Task OnCategoryFilterChanged(IEnumerable<string> values)
    {
        selectedCategoryFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        selectedRows = [];
        await ReloadAsync();
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.CategoryNames = values?.ToList() ?? [];
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        selectedRows = [];
        logger.LogInformation("Meeting search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Meeting refresh triggered.");
        await RefreshJobsAsync();
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
        CurrentRecord = NewRecord();
        ResetUploadState();
        isNewRecordMode = true;
        modalTitle = "新增會議紀錄";
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord, DescribePendingMedia());
        modalVisible = true;
        logger.LogInformation("Opened create modal for meeting.");
        return Task.CompletedTask;
    }

    private Task OnEditAsync(MeetingDto meeting)
    {
        isNewRecordMode = false;
        modalTitle = "修改會議紀錄";
        CurrentRecord = Clone(meeting);
        ResetUploadState();
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord, DescribePendingMedia());
        modalVisible = true;
        logger.LogInformation("Opened edit modal for meeting. MeetingId={MeetingId}, Title={Title}", meeting.Id, meeting.Title);
        return Task.CompletedTask;
    }

    /// <summary>編輯前複製一份（取消時不影響清單）。表單只改中繼資料，其他欄位照抄給畫面判斷用。</summary>
    private static MeetingDto Clone(MeetingDto source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        MeetingDate = source.MeetingDate,
        Description = source.Description,
        Categories = source.Categories,
        CategoryNames = [.. source.CategoryNames ?? []],
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        MediaOriginalFileName = source.MediaOriginalFileName,
        MediaFileSize = source.MediaFileSize,
        TranscriptionStatus = source.TranscriptionStatus,
        ProjectId = source.ProjectId,
        ProjectTitle = source.ProjectTitle,
        CreatedByUserId = source.CreatedByUserId,
        HasMedia = source.HasMedia,
        HasTranscript = source.HasTranscript,
        IsUploader = source.IsUploader,
        CanDelete = source.CanDelete,
    };

    #region 多選刪除（1.0.53）

    /// <summary>目前這一頁勾選的列。換頁、排序、搜尋、篩選時清掉；背景重新載入時依 Id 保留。</summary>
    private IEnumerable<MeetingDto> selectedRows = [];

    private bool isBatchDeleting;

    /// <summary>能勾選＝能刪除（上傳者或專案負責人＋會議紀錄 edit，由 API 算好），而且不在上傳中。</summary>
    private bool CanSelect(MeetingDto meeting)
        => meeting.CanDelete && GetLiveProgress(IdOf(meeting)) is not { IsUploading: true };

    /// <summary>
    /// 有會議紀錄「編輯」權限才顯示勾選欄（刪除要求的就是 edit）。
    /// ⚠️ 1.0.59：只看權限，**不可以看資料**。AntDesign 1.6 的 Selection 在勾選欄一下有一下沒有時，
    /// 清掉列上的 Selection 會丟 NullReferenceException。不能刪的列靠 Disabled 擋；TableSelectionColumnTests 會擋回頭路。
    /// </summary>
    private bool ShowSelection => PermissionView.Can(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit);

    private async Task OnBatchDeleteAsync()
    {
        var items = selectedRows.Where(CanSelect).ToList();
        if (items.Count == 0 || isBatchDeleting)
        {
            return;
        }

        // 寫清楚會刪什麼、留下什麼（同單筆刪除）：待辦保留，只是不再連到會議。
        var linkedTodos = 0;
        foreach (var item in items)
        {
            linkedTodos += (await MeetingApi.CountLinkedTodosAsync(IdOf(item))).Data;
        }

        var todoNote = linkedTodos > 0
            ? $"從這些會議抽出的 {linkedTodos} 筆待辦會保留在專案裡（不再連到會議）。"
            : string.Empty;

        var ok = await modalService.ConfirmAsync(new ConfirmOptions()
        {
            Title = "確認刪除",
            Content = $"確定要刪除勾選的 {items.Count} 筆會議紀錄嗎？影音檔、逐字稿、會議紀錄與 AI 問答會一併刪除。{todoNote}此操作無法復原。",
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
            // 逐筆走單筆刪除：轉錄或生成中的會被服務擋下，原因列在通知裡。
            var result = await BatchRunner.RunAsync(items, x => x.Title, async x =>
            {
                var r = await MeetingApi.DeleteAsync(IdOf(x));
                return r.Success ? null : MeetingErrorText(r);
            });
            logger.LogInformation("Batch meeting delete completed. Succeeded={Succeeded}, Failed={Failed}", result.Succeeded, result.Failures.Count);
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

    private async Task OnDeleteAsync(MeetingDto meeting)
    {
        logger.LogInformation("Delete meeting requested. MeetingId={MeetingId}, Title={Title}", meeting.Id, meeting.Title);

        // 1.0.47：寫清楚會刪什麼、留下什麼——待辦是正在追蹤的工作，刪會議時保留。
        var linkedTodos = (await MeetingApi.CountLinkedTodosAsync(IdOf(meeting))).Data;
        var todoNote = linkedTodos > 0
            ? $"從這場會議抽出的 {linkedTodos} 筆待辦會保留在專案裡（不再連到這場會議）。"
            : string.Empty;

        var ok = await modalService.ConfirmAsync(new ConfirmOptions()
        {
            Title = "確認刪除",
            Content = $"確定要刪除這筆會議紀錄嗎？影音檔、逐字稿、會議紀錄與 AI 問答會一併刪除。{todoNote}此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Meeting delete cancelled by user. MeetingId={MeetingId}", meeting.Id);
            return;
        }

        // 0.4.115 以前不看結果，失敗（包括背景工作進行中被擋下）也顯示「刪除成功」。
        var deleteResult = await MeetingApi.DeleteAsync(IdOf(meeting));
        if (!deleteResult.Success)
        {
            NotifyError(MeetingErrorText(deleteResult));
            return;
        }

        logger.LogInformation("Meeting delete completed. MeetingId={MeetingId}", meeting.Id);

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "刪除成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        await ReloadAsync();
    }

    /// <summary>
    /// 表單送出閘門（1.0.62）：「確定」與 Enter 共用，等候期間也算在送出中，連按不會新增兩筆。
    /// </summary>
    private readonly FormSubmitGate submitGate = new();

    /// <summary>存檔中（含 Enter 等候回寫）。</summary>
    private bool isSaving => submitGate.IsBusy;

    /// <summary>
    /// 存檔中整張表單鎖住（1.0.25）：欄位、移除檔案、取消鈕、右上角關閉、Esc 都不能用。
    /// 1.0.27 起上傳在背景跑、存好主資料就關窗，所以上傳期間不鎖表單（只鎖正在上傳的那一列）。
    /// </summary>
    private bool IsFormLocked => isSaving;

    private Task OnModalOKHandleAsync(MouseEventArgs args) => submitGate.RunAsync(SaveModalAsync);

    /// <summary>
    /// 存檔。視窗是單向 Visible（1.1.19）：被擋下時什麼都不用做，視窗本來就還開著；成功才關。
    /// </summary>
    private async Task SaveModalAsync()
    {
        // 這次存檔才新增出來的會議，上傳被取消時連會議一起刪（1.0.37）；下面新增成功後 isNewRecordMode 會被改掉，要先記下。
        var createdInThisSave = isNewRecordMode;

        if (LocalEditContext?.Validate() == false)
        {
            foreach (var error in LocalEditContext.GetValidationMessages())
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

            if (!await modalService.ConfirmAsync(uploadConfirmOptions))
            {
                logger.LogDebug("Media upload cancelled by user at cost confirmation. MeetingId={MeetingId}", CurrentRecord.Id);
                return;
            }
        }

        var payload = new MeetingCreateUpdateDto
        {
            Id = CurrentRecord.Id,
            Title = CurrentRecord.Title,
            MeetingDate = CurrentRecord.MeetingDate,
            Description = CurrentRecord.Description,
            CategoryNames = [.. CurrentRecord.CategoryNames ?? []],
        };

        if (isNewRecordMode)
        {
            var addResult = await MeetingApi.CreateAsync(payload);
            if (!addResult.Success || addResult.Data is not { } created)
            {
                NotifyError(MeetingErrorText(addResult));
                return;
            }

            CurrentRecord.Id = created.Id;
            logger.LogInformation("Meeting create submitted. MeetingId={MeetingId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            NotifySuccess("新增成功");
            _ = messageService.SuccessAsync("新增成功");

            // ⚠️ 這一筆已經存進資料庫了，所以不能再算是「新增模式」：下面若還要重試，不能再新增一次。
            isNewRecordMode = false;
        }
        else
        {
            var updateResult = await MeetingApi.UpdateAsync(payload);
            if (!updateResult.Success)
            {
                NotifyError(MeetingErrorText(updateResult));
                return;
            }

            logger.LogInformation("Meeting update submitted. MeetingId={MeetingId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            NotifySuccess("修改成功");
        }

        // 主資料存好之後才上傳影音檔——新增模式下要等伺服器給 Id 才知道檔案掛在哪一筆。
        // 1.0.27：存好主資料就關窗，上傳在背景跑，進度直接顯示在清單這一列的「轉錄狀態」。
        modalVisible = false;
        formSnapshot = null;
        if (pendingMediaFile is { } file && mediaDropZone?.InputElement is { } input)
        {
            // StartAsync 在呼叫 JS 的當下就把檔案交出去了，之後才能重設拖拉區。
            var startError = await UploadManager.StartAsync(input, file, IdOf(CurrentRecord), deleteMeetingOnCancel: createdInThisSave);
            if (startError is not null)
            {
                NotifyError($"{startError}會議資料已保留，要重新上傳請按這一列的「修改」再選一次檔案。");
            }
        }

        ResetUploadState();
        await ReloadAsync();
    }

    /// <summary>
    /// 待上傳影音檔的識別。
    /// ⚠️ 不能把 <see cref="IBrowserFile"/> 本身交給 <see cref="FormDirtyHelper.Capture"/>——
    /// 它身上帶著 Stream，序列化出來的東西與使用者選了哪個檔案無關，**所有檔案都會長得一樣**。檔名＋大小才是識別。
    /// </summary>
    private string? DescribePendingMedia()
        => pendingMediaFile is null ? null : $"{pendingMediaFile.Name}:{pendingMediaFile.Size}";

    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        // 存檔中不該關窗，這道早退要留在最前面（先於 dirty 判斷）。
        if (isSaving || isDiscardConfirming)
        {
            return;
        }

        // ⚠️ 只拖了檔案、一個欄位都沒改，也算改過——所以要把待上傳檔案一起比。
        if (FormDirtyHelper.IsDirty(formSnapshot, CurrentRecord, DescribePendingMedia()))
        {
            isDiscardConfirming = true;
            bool discard;
            try
            {
                discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆會議紀錄");
            }
            finally
            {
                isDiscardConfirming = false;
            }

            if (!discard)
            {
                return;
            }
        }

        modalVisible = false;
        formSnapshot = null;
        ResetUploadState();
        logger.LogDebug("Meeting modal cancelled.");
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        // Shift+Enter 會落回瀏覽器原生的換行，而組字中的 Enter 被 IsComposing 擋掉（見 FormKeyboardHelper）。
        if (FormKeyboardHelper.IsSubmit(args))
        {
            // 等候不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，Enter 送出時焦點還在欄位裡，不等就會拿到舊值。
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

    #region 影音檔上傳與轉錄

    private Task OnMediaFileSelectedAsync(InputFileChangeEventArgs args)
    {
        var file = args.File;

        if (!MeetingMediaPolicy.IsAllowedFileName(file.Name))
        {
            NotifyError($"「{file.Name}」不是支援的影音格式，允許的格式：{MeetingMediaPolicy.AllowedExtensionsText}。");

            // 先前挑的檔案要一起清掉（0.4.115）：input 已經換成這個不合法的檔，留著的話畫面仍顯示「待上傳」，上傳卻一定失敗。
            ClearPendingMediaFile();
            return Task.CompletedTask;
        }

        if (file.Size > MeetingMediaPolicy.MaxUploadFileSize)
        {
            NotifyError($"「{file.Name}」超過單檔上限 {MeetingMediaPolicy.FormatFileSize(MeetingMediaPolicy.MaxUploadFileSize)}。");
            ClearPendingMediaFile();
            return Task.CompletedTask;
        }

        pendingMediaFile = file;
        logger.LogInformation("Meeting media file selected. FileName={FileName}, FileSize={FileSize}", file.Name, file.Size);
        return Task.CompletedTask;
    }

    /// <summary>底下那個拖拉區。用完檔案之後要呼叫 Reset()，「移除 → 再拖同一個檔案」才會有反應。</summary>
    private FileDropZone? mediaDropZone;

    private void ClearPendingMediaFile()
    {
        pendingMediaFile = null;

        // ⚠️ 只能在確定不再需要那個檔案之後重建 input。在「剛選完檔案」時重建，會把檔案自己弄丟——見 FileDropZone 的註解。
        mediaDropZone?.Reset();
    }

    /// <summary>
    /// 上傳期間唯一能按的按鈕（1.0.25）。不提供「暫停」：伺服器超過 2 分鐘沒收到下一塊就當成中斷。
    /// </summary>
    private async Task OnCancelUploadAsync(int meetingId)
    {
        var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "取消上傳",
            Content = UploadManager.DeletesMeetingOnCancel(meetingId)
                ? "已上傳的部分會被丟棄，這筆剛新增的會議紀錄也會一併刪除。確定要取消嗎？"
                : "已上傳的部分會被丟棄，會議資料會保留，之後可以重新上傳。確定要取消嗎？",
            OkText = "取消上傳",
            CancelText = "繼續上傳",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!confirmed)
        {
            return;
        }

        // 確認框開著時上傳可能已經跑完了（1.0.37 補上通知）；取消成功的通知由上傳管理在結束時跳。
        logger.LogInformation("Meeting media upload cancellation requested. MeetingId={MeetingId}", meetingId);
        if (!await UploadManager.CancelAsync(meetingId))
        {
            NotifySuccess(MeetingUploadManager.UploadTooLateToCancelMessage);
        }
    }

    private async Task OnRetryTranscriptionAsync(MeetingDto meeting)
    {
        if (requeueingMeetingId is not null)
        {
            return;
        }

        logger.LogInformation("Retry transcription requested. MeetingId={MeetingId}", meeting.Id);

        // Danger 跟「覆蓋」走、不跟「花錢」走（見 開發慣例與限制速查 的 UI 慣例）：
        // 只有已完成的那一支會刪掉現有逐字稿，才算破壞性動作。
        var status = (TranscriptionStatus)meeting.TranscriptionStatus;
        var willOverwrite = status == TranscriptionStatus.Completed;

        var situation = status switch
        {
            TranscriptionStatus.Completed
                => $"「{meeting.Title}」已經有逐字稿了，重新轉錄會覆蓋現有逐字稿，舊檔案會被刪除且無法復原。",
            TranscriptionStatus.Failed
                => $"「{meeting.Title}」上次轉錄失敗，這會重新跑一次完整的轉錄。",
            TranscriptionStatus.Cancelled
                => $"「{meeting.Title}」上次轉錄已取消，這會從頭重跑，不會接續上次的進度。",
            _ => $"這會為「{meeting.Title}」執行一次完整的轉錄。",
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

        if (!await modalService.ConfirmAsync(confirmOptions))
        {
            logger.LogDebug("Retry transcription cancelled by user. MeetingId={MeetingId}", meeting.Id);
            return;
        }

        requeueingMeetingId = meeting.Id;
        try
        {
            var result = await MeetingApi.RetranscribeAsync(IdOf(meeting));
            if (!result.Success)
            {
                NotifyError(MeetingErrorText(result));
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

    /// <summary>
    /// 清掉待上傳的檔案並重建拖拉區。1.1.23 起上傳開始時 JS 已拿到檔案本身，重建拖拉區不影響進行中的上傳。
    /// </summary>
    private void ResetUploadState()
    {
        pendingMediaFile = null;
        mediaDropZone?.Reset();
    }

    #endregion

    /// <summary>取這一列目前進行中的轉錄（含上傳）；沒有就回 null（狀態欄只顯示徽章）。</summary>
    private BackgroundJobDto? GetLiveProgress(int meetingId)
        => transcriptionJobs.TryGetValue(meetingId, out var job) && job.IsRunning ? job : null;

    private BackgroundJobDto? GetLiveDraftProgress(int meetingId)
        => draftJobs.TryGetValue(meetingId, out var job) && job.IsRunning ? job : null;

    #region AI 會議紀錄

    /// <summary>開啟「產生會議紀錄」對話框（1.0.48 前叫「AI 轉會議紀錄」）。專案與提示詞清單在這裡才撈，確保是最新的。</summary>
    private async Task OnOpenDraftRequestAsync(MeetingDto meeting)
    {
        projects = (await MeetingApi.GetSelectableProjectsAsync()).Data ?? [];
        promptTemplates = (await MeetingApi.GetSelectablePromptTemplatesAsync()).Data ?? [];

        draftRequestMeetingId = IdOf(meeting);
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
    /// 「產生會議紀錄」對話框的 Enter 送出。
    /// ⚠️ 走的是與按鈕完全相同的 <see cref="OnDraftRequestOkAsync"/>，所以那道費用二次確認照樣會跳。
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
            return;
        }

        isGenerating = true;
        try
        {
            logger.LogInformation(
                "Draft generation requested from meeting page. MeetingId={MeetingId}, ProjectId={ProjectId}, PromptTemplateId={PromptTemplateId}",
                draftRequestMeetingId,
                draftRequestProjectId,
                draftRequestPromptTemplateId);

            var result = await MeetingApi.RequestDraftAsync(draftRequestMeetingId, new MeetingDraftRequestDto
            {
                PromptTemplateId = draftRequestPromptTemplateId,
                ProjectId = draftRequestProjectId > 0 ? draftRequestProjectId : null,
                Attendees = draftRequestAttendees,
            });

            if (!result.Success)
            {
                NotifyError(MeetingErrorText(result));
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
    private async Task OnOpenAttachAsync(MeetingDto meeting)
    {
        projects = (await MeetingApi.GetSelectableProjectsAsync()).Data ?? [];

        attachMeetingId = IdOf(meeting);
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
            return;
        }

        isAttaching = true;
        try
        {
            var result = await MeetingApi.AttachToProjectAsync(attachMeetingId, attachProjectId);
            if (!result.Success)
            {
                NotifyError(MeetingErrorText(result));
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

    /// <summary>
    /// 開啟「會議紀錄」視窗（1.0.40 起操作欄只有這一顆）：先唯讀檢視，可以切到逐字稿；
    /// 有權限時按「編修」在同一個視窗裡切成左改右看。
    ///
    /// <para>
    /// 已歸屬專案的會議在這一頁**不能**編修也不能下載 PDF，所以視窗底部要講清楚去哪裡做——
    /// 沒有這行說明，使用者只會覺得按鈕莫名其妙消失了。
    /// </para>
    /// </summary>
    private async Task OnOpenDraftModalAsync(MeetingDto meeting)
    {
        // 清單不帶會議紀錄全文（太大），開視窗時才取單筆。
        var detail = await MeetingApi.GetAsync(IdOf(meeting));
        if (!detail.Success || detail.Data is not { } current)
        {
            NotifyError(MeetingErrorText(detail));
            return;
        }

        draftMeeting = current;
        draftMeetingId = IdOf(current);
        draftModalTitle = $"會議紀錄 - {current.Title}";
        draftContent = current.DraftContent;
        draftCanEdit = false;
        draftNotice = current.HasDraft && !IsUnassigned(current)
            ? $"這份會議紀錄已歸屬「{ProjectTitleText(current)}」專案，編修與下載 PDF 請到「專案項目」頁。"
            : null;

        // ⚠️ 只有**未歸屬專案**的會議能在這裡編修（0.4.82）：已歸屬的編修與匯出都集中在專案項目頁，
        // 那裡才有專案脈絡（常用名詞、與會人員），PDF 表頭也要帶專案名稱。
        draftCanSwitchToEdit = current.HasDraft && IsUnassigned(current) && CanEdit;

        // PDF 只在未歸屬時從這一頁下載：已歸屬的要帶專案名稱進表頭，那是專案頁才有的脈絡。
        draftCanExport = IsUnassigned(current) && current.HasDraft && CanExport;

        // 逐字稿不分歸屬都能看；複製、下載算匯出，要有匯出權限。
        draftTranscript = current.HasTranscript ? (await MeetingApi.GetTranscriptAsync(IdOf(current))).Data?.Text : null;
        draftCanCopyTranscript = CanExport;

        // 1.0.48：產生／重新產生從這個視窗進（列上的「AI 轉會議紀錄」已拿掉）。生成中、沒有逐字稿時不出現。
        draftCanGenerate = current.CanGenerateDraft && CanEdit;

        draftModalVisible = true;
    }

    /// <summary>
    /// 視窗裡的「產生會議紀錄」／「重新產生」（1.0.48）：先關掉這個視窗，再開原本的設定視窗
    /// （提示詞、所屬專案、與會人員、費用確認都沿用）。進度在清單的「會議紀錄」欄看。
    /// </summary>
    private async Task OnGenerateFromDraftModalAsync()
    {
        if (draftMeeting is null)
        {
            return;
        }

        draftModalVisible = false;
        await OnOpenDraftRequestAsync(draftMeeting);
    }

    private void OnRequestDraftEdit()
    {
        draftCanEdit = true;
        draftModalTitle = $"編修會議紀錄 - {draftMeeting?.Title}";
    }

    private void OnCancelDraftEdit()
    {
        draftCanEdit = false;
        draftModalTitle = $"會議紀錄 - {draftMeeting?.Title}";
    }

    /// <summary>逐字稿「複製全文」：給使用者貼到外部 AI 問答（1.0.40 加回逐字稿的原因）。稽核由伺服器寫。</summary>
    private async Task OnCopyTranscriptAsync()
    {
        if (draftMeeting is null || string.IsNullOrEmpty(draftTranscript))
        {
            return;
        }

        if (!await clipboardInterop.CopyTextAsync(draftTranscript))
        {
            NotifyError("瀏覽器不允許自動複製，請在逐字稿上按 Ctrl+A 全選後再按 Ctrl+C。");
            return;
        }

        await MeetingApi.TranscriptCopiedAsync(IdOf(draftMeeting));
        _ = messageService.SuccessAsync("已複製逐字稿。");
    }

    /// <summary>逐字稿「下載 .txt」：伺服器產生（UTF-8 含 BOM，記事本直接開也不亂碼）並寫稽核。</summary>
    private async Task OnDownloadTranscriptAsync()
    {
        if (draftMeeting is null || string.IsNullOrEmpty(draftTranscript))
        {
            return;
        }

        var download = await MeetingApi.DownloadTranscriptAsync(IdOf(draftMeeting));
        if (!download.Success || download.Content is null)
        {
            NotifyError($"下載逐字稿失敗：{download.Error}");
            return;
        }

        await fileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
        NotifySuccess($"已下載「{download.FileName}」。");
    }

    private async Task OnDraftSavedAsync(string content)
    {
        isSavingDraft = true;
        try
        {
            var result = await MeetingApi.UpdateDraftAsync(draftMeetingId, content);
            if (!result.Success)
            {
                // 存檔失敗時視窗留著，使用者的編修還在裡面。
                NotifyError(MeetingErrorText(result));
                return;
            }

            // 1.0.40：存好回到檢視（視窗不關），顯示存好的內容。
            draftContent = content;
            if (draftMeeting is not null)
            {
                draftMeeting.DraftContent = content;
            }
            OnCancelDraftEdit();
            NotifySuccess("會議紀錄已儲存。");
            await ReloadAsync();
        }
        finally
        {
            isSavingDraft = false;
        }
    }

    /// <summary>
    /// 視窗裡的「下載 PDF」。
    /// ⚠️ 送的是**視窗裡當下的內容**：使用者改了字、還沒儲存就按下載時，資料庫裡是舊版本，
    /// 產出來的 PDF 會與畫面不符而且看不出來。伺服器只拿它產這一次 PDF，不回寫。
    /// </summary>
    private async Task OnExportCurrentDraftAsync(string content)
    {
        if (draftMeeting is null || exportingMeetingId is not null)
        {
            return;
        }

        exportingMeetingId = draftMeeting.Id;
        StateHasChanged();
        try
        {
            var download = await MeetingApi.DownloadDraftPdfAsync(IdOf(draftMeeting), content);
            if (!download.Success || download.Content is null)
            {
                NotifyError($"匯出 PDF 失敗：{download.Error}");
                return;
            }

            await fileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
            logger.LogInformation("Meeting draft exported as pdf from meeting page. MeetingId={MeetingId}, FileName={FileName}", draftMeeting.Id, download.FileName);
            NotifySuccess($"已下載「{download.FileName}」。");
        }
        finally
        {
            exportingMeetingId = null;
            StateHasChanged();
        }
    }

    /// <summary>就單一會議提問：讀該會議的會議紀錄與逐字稿，與專案無關。</summary>
    private void OpenMeetingChat(MeetingDto meeting)
    {
        aiChatTargetId = IdOf(meeting);
        aiChatTitle = $"AI 問答 - {meeting.Title}";
        aiChatVisible = true;
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

    /// <summary>失敗原因：新增、修改、刪除沿用既有對外 API 的格式（放在 <c>errors.Validation</c>），新端點放 <c>errorMessage</c>，兩種都認。</summary>
    private static string MeetingErrorText<T>(MeetingRecord.Dtos.Commons.ApiResult<T> result) => ApiClient.ErrorText(result);

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

    /// <summary>元件已釋放：推播或上傳結束時，使用者可能已經離開這一頁。</summary>
    private bool disposed;

    public void Dispose()
    {
        // 不取消上傳：離開這一頁上傳照樣繼續（1.1.23 起換到其他 WASM 頁也不中斷），右下角面板看得到進度。
        disposed = true;
        UploadManager.Changed -= OnUploadsChanged;
        jobSubscription?.Dispose();
    }
}
