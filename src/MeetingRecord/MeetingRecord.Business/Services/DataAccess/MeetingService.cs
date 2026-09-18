using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 會議紀錄的 Blazor 服務層。CRUD 骨架與團隊列級權控比照 <see cref="PromptTemplateService"/>；
/// 影音檔與逐字稿的實體檔案操作一律委派給 <see cref="MeetingFileStore"/>。
/// </summary>
public class MeetingService
{
    /// <summary>
    /// 單次生成最多帶幾位與會者。純粹是為了界定提示詞大小，不是權限限制——
    /// 一場會議列到 50 個名字已經遠超過模型能有效利用的範圍。
    /// </summary>
    private const int MaxDraftAttendees = 50;

    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly MeetingFileStore fileStore;
    private readonly AiChatStore chatStore;
    private readonly ITranscriptionQueue transcriptionQueue;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private readonly IMeetingDraftQueue draftQueue;
    private readonly IMeetingDraftProgressNotifier draftProgressNotifier;
    private readonly CurrentUserService currentUserService;

    public IMapper Mapper { get; }
    public ILogger<MeetingService> Logger { get; }

    public MeetingService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MeetingService> logger,
        IRecordAccessScopeProvider accessScope,
        MeetingFileStore fileStore,
        AiChatStore chatStore,
        ITranscriptionQueue transcriptionQueue,
        ITranscriptionProgressNotifier progressNotifier,
        IMeetingDraftQueue draftQueue,
        IMeetingDraftProgressNotifier draftProgressNotifier,
        CurrentUserService currentUserService)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
        this.fileStore = fileStore;
        this.chatStore = chatStore;
        this.transcriptionQueue = transcriptionQueue;
        this.progressNotifier = progressNotifier;
        this.draftQueue = draftQueue;
        this.draftProgressNotifier = draftProgressNotifier;
        this.currentUserService = currentUserService;
    }

    /// <summary>
    /// 組出背景工作的請求，把「是誰按的」一起帶進佇列。
    ///
    /// <para>
    /// ⚠️ 必須在這裡取使用者。這支跑在 Blazor circuit 裡，CurrentUserService 是填好的；
    /// 但背景服務為每筆工作自建的 scope 裡它是空白物件（Id=0），
    /// 等到 runner 才去問，用量帳本的「誰」就永遠是空的。
    /// </para>
    /// </summary>
    private MeetingJobRequest BuildJobRequest(int meetingId)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);

        return new MeetingJobRequest(meetingId, userId, userName);
    }

    #region 查詢

    public async Task<DataRequestResult<MeetingAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading meetings. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MeetingAdapterModel> result = new();

        // Include(Project) 是為了讓清單能顯示「所屬專案」。ProjectTitle 是跨物件欄位，
        // AutoMapper 的同名慣例對應不到，少了這一段整欄會全部顯示「— 未歸屬」。
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking().Include(x => x.Project);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                (x.MediaOriginalFileName ?? string.Empty).Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Meeting>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Meeting>(x => x.Teams, dataRequest.TeamFilters));
        }

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Meeting>(x => x.Teams, scope.Teams));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MeetingAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.MeetingDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.MeetingDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.MeetingDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.TranscriptionStatus))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.TranscriptionStatus).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.TranscriptionStatus).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.UpdatedAt))
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
            dataSource = dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<Meeting> records = await dataSource.ToListAsync();
        result.Result = MapWithProjectTitle(records);
        Logger.LogDebug("Loaded meetings successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MeetingAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading meeting by id. MeetingId={MeetingId}", id);

        Meeting? item = await context.Meeting
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Meeting not found. MeetingId={MeetingId}", id);
            return new MeetingAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Meeting access denied by team scope. MeetingId={MeetingId}", id);
            return new MeetingAdapterModel();
        }

        return Mapper.Map<MeetingAdapterModel>(item);
    }

    #endregion

    #region 新增 / 修改 / 刪除

    /// <summary>
    /// 新增會議紀錄。成功時把資料庫產生的 Id 回填到 <paramref name="paraObject"/>，
    /// 讓 UI 可以接著把影音檔掛到這筆紀錄上。
    /// </summary>
    public async Task<VerifyRecordResult> AddAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogInformation("Creating meeting. Title={Title}", paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting itemParameter = Mapper.Map<Meeting>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Meeting.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            paraObject.Id = itemParameter.Id;

            Logger.LogInformation("Meeting created successfully. MeetingId={MeetingId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create meeting. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 修改會議紀錄。影音檔與轉錄相關欄位一律沿用資料庫既有值，
    /// 避免畫面上的舊複本把背景轉錄剛寫入的狀態蓋掉。
    /// </summary>
    public async Task<VerifyRecordResult> UpdateAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogInformation("Updating meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? item = await context.Meeting
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Meeting update rejected because record was not found. MeetingId={MeetingId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的會議紀錄。");
            }

            Meeting itemData = Mapper.Map<Meeting>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            itemData.MediaOriginalFileName = item.MediaOriginalFileName;
            itemData.MediaStoredFileName = item.MediaStoredFileName;
            itemData.MediaRelativePath = item.MediaRelativePath;
            itemData.MediaContentType = item.MediaContentType;
            itemData.MediaFileSize = item.MediaFileSize;
            itemData.TranscriptRelativePath = item.TranscriptRelativePath;
            itemData.TranscriptionStatus = item.TranscriptionStatus;
            itemData.TranscriptionError = item.TranscriptionError;
            itemData.TranscriptionStartedAt = item.TranscriptionStartedAt;
            itemData.TranscriptionCompletedAt = item.TranscriptionCompletedAt;

            // 草稿同樣由背景工作寫入，畫面上的舊複本不得覆寫。
            itemData.DraftContent = item.DraftContent;
            itemData.DraftStatus = item.DraftStatus;
            itemData.DraftError = item.DraftError;
            itemData.DraftPromptTemplateId = item.DraftPromptTemplateId;
            itemData.DraftPromptTemplateName = item.DraftPromptTemplateName;
            itemData.DraftStartedAt = item.DraftStartedAt;
            itemData.DraftCompletedAt = item.DraftCompletedAt;

            // DraftAttendees 在 AdapterModel 上根本沒有對應屬性，Mapper 一定產出 null；
            // 不沿用的話，光是在畫面上改個標題就會把與會者快照清掉。
            itemData.DraftAttendees = item.DraftAttendees;

            // ProjectId 從 0.4.73 起也一併沿用。歸屬的權威路徑已經是
            // AttachToProjectAsync／DetachFromProjectAsync 兩支方法，不再是這張表單；
            // 讓畫面上的舊複本寫回去，只會在別處剛改過歸屬時把它靜默退回舊值。
            itemData.ProjectId = item.ProjectId;

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", itemData.Id, itemData.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除會議紀錄，並移除影音檔與逐字稿的實體檔案。
    /// 實體檔案刪除失敗只記 Warning，不阻斷資料列刪除（見 docs/features/檔案上傳機制.md §4）。
    /// </summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting meeting. MeetingId={MeetingId}", id);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? item = await context.Meeting
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Meeting deletion rejected because record was not found. MeetingId={MeetingId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的會議紀錄。");
            }

            var mediaRelativePath = item.MediaRelativePath;
            var transcriptRelativePath = item.TranscriptRelativePath;

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            fileStore.TryDeleteMedia(mediaRelativePath);
            fileStore.TryDeleteTranscript(transcriptRelativePath);

            // 0.4.60 起對話存在檔案系統，資料表已移除，Cascade 不會再幫我們清掉它。
            chatStore.TryDelete(AiChatScope.Meeting, id);

            Logger.LogInformation("Meeting deleted successfully. MeetingId={MeetingId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete meeting. MeetingId={MeetingId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除會議紀錄失敗。", ex);
        }
    }

    #endregion

    #region 前置檢查

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for meeting. Title={Title}", paraObject.Title);

        if (string.IsNullOrWhiteSpace(paraObject.Title))
        {
            return VerifyRecordResultFactory.Build(false, "會議標題不可為空白。");
        }

        return await Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<Meeting>(context);
        var searchItem = await context.Meeting
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because meeting was not found. MeetingId={MeetingId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的會議紀錄不存在。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    #endregion

    #region 影音檔與逐字稿

    /// <summary>
    /// 上傳（或替換）會議影音檔：落地實體檔案 → 更新中繼資料並標記為待處理 → 排入轉錄佇列。
    /// <paramref name="progress"/> 會在複製過程中回報 0-100 的百分比，供畫面顯示進度列。
    /// </summary>
    public async Task<VerifyRecordResult> SaveMediaAsync(
        int meetingId,
        MeetingMediaUploadInput uploadFile,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Logger.LogInformation(
            "Uploading meeting media. MeetingId={MeetingId}, FileName={FileName}, FileSize={FileSize}",
            meetingId,
            uploadFile.FileName,
            uploadFile.FileSize);

        if (!MeetingMediaPolicy.IsAllowedFileName(uploadFile.FileName))
        {
            return VerifyRecordResultFactory.Build(false, $"不支援的檔案格式，允許的格式：{MeetingMediaPolicy.AllowedExtensionsText}。");
        }

        if (uploadFile.FileSize > MeetingMediaPolicy.MaxUploadFileSize)
        {
            return VerifyRecordResultFactory.Build(
                false,
                $"影音檔大小不可超過 {MeetingMediaPolicy.FormatFileSize(MeetingMediaPolicy.MaxUploadFileSize)}。");
        }

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                Logger.LogWarning("Meeting media upload rejected because record was not found. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "找不到要上傳影音檔的會議紀錄。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Meeting media upload denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限對這筆會議紀錄上傳影音檔。");
            }

            var previousMediaRelativePath = meeting.MediaRelativePath;
            var previousTranscriptRelativePath = meeting.TranscriptRelativePath;

            var stored = await fileStore.SaveMediaAsync(meeting.CreatedAt, uploadFile, progress, cancellationToken);

            meeting.MediaOriginalFileName = stored.OriginalFileName;
            meeting.MediaStoredFileName = stored.StoredFileName;
            meeting.MediaRelativePath = stored.RelativePath;
            meeting.MediaContentType = stored.ContentType;
            meeting.MediaFileSize = stored.FileSize;
            meeting.TranscriptRelativePath = null;
            meeting.TranscriptionStatus = TranscriptionStatus.Pending;
            meeting.TranscriptionError = null;
            meeting.TranscriptionStartedAt = null;
            meeting.TranscriptionCompletedAt = null;

            // 會議日期沒填就帶入上傳當天；已填的不覆蓋，要更正仍可從畫面編輯。
            meeting.MeetingDate ??= DateTime.Today;

            meeting.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            // 新檔案登錄成功後才清掉舊的影音檔與逐字稿。
            fileStore.TryDeleteMedia(previousMediaRelativePath);
            fileStore.TryDeleteTranscript(previousTranscriptRelativePath);

            // 先登錄進度再入列：背景工作要等輪到才會知道這件事，先登錄畫面才立刻看得到「排隊中」。
            progressNotifier.Enqueued(meetingId, meeting.Title, meeting.Teams);
            await transcriptionQueue.EnqueueAsync(BuildJobRequest(meetingId), cancellationToken);

            Logger.LogInformation("Meeting media uploaded and queued for transcription. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to upload meeting media. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "影音檔上傳失敗。", ex);
        }
    }

    /// <summary>
    /// 重新把會議排入轉錄佇列（供「重新轉錄」按鈕使用）。
    /// 已排入（待處理）或正在轉錄中的紀錄一律拒絕，否則會重複入列、跑兩趟並重複計費。
    /// 佇列不持久化，重啟殘留的「待處理」由 Program.cs 的啟動修復改判為「失敗」後才能從這裡重跑。
    /// </summary>
    public async Task<VerifyRecordResult> RequeueTranscriptionAsync(int meetingId, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Requeueing transcription. MeetingId={MeetingId}", meetingId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要轉錄的會議紀錄。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Transcription requeue denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限對這筆會議紀錄執行轉錄。");
            }

            if (string.IsNullOrWhiteSpace(meeting.MediaRelativePath))
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄尚未上傳影音檔。");
            }

            // Pending 也要擋：第一次點完狀態就變 Pending，只擋 Processing 會讓同一筆入列兩次、
            // 被單一 worker 依序跑兩趟完整轉錄。進度面板以 Kind-MeetingId 為 key 又把兩筆收成
            // 一列，使用者看不出跑了兩趟——帳單才看得出來。
            if (meeting.TranscriptionStatus is TranscriptionStatus.Processing or TranscriptionStatus.Pending)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄已排入轉錄，請稍候。");
            }

            meeting.TranscriptionStatus = TranscriptionStatus.Pending;
            meeting.TranscriptionError = null;
            meeting.TranscriptionStartedAt = null;
            meeting.TranscriptionCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            progressNotifier.Enqueued(meetingId, meeting.Title, meeting.Teams);
            await transcriptionQueue.EnqueueAsync(BuildJobRequest(meetingId), cancellationToken);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to requeue transcription. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "重新轉錄失敗。", ex);
        }
    }

    #endregion

    #region AI 會議紀錄草稿

    /// <summary>
    /// 把一份未歸屬的會議紀錄事後補歸屬到專案。
    ///
    /// <para>
    /// 存在的理由是使用者可以不必先建專案就產生會議紀錄（從「會議紀錄」頁直接生成），
    /// 事後才想歸檔。這支<b>只寫 <c>ProjectId</c></b>：草稿內容、提示詞快照、與會者快照、
    /// 生成時間全部原封不動，<b>不重新生成、不呼叫任何付費 API</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它<b>不是</b> <see cref="DetachFromProjectAsync"/> 的可逆反向操作：移除會連草稿一起刪，
    /// 歸屬回去救不回來。
    /// </para>
    /// </summary>
    /// <param name="projectId">要歸屬的目標專案。已歸屬其他專案的會被拒絕，必須先從該專案移除。</param>
    public async Task<VerifyRecordResult> AttachToProjectAsync(
        int meetingId,
        int projectId,
        CancellationToken cancellationToken = default)
    {
        Logger.LogInformation(
            "Attaching meeting to project. MeetingId={MeetingId}, ProjectId={ProjectId}", meetingId, projectId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要歸屬的會議紀錄。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Attach denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限變更這筆會議紀錄的歸屬。");
            }

            // 兩種「已歸屬」要分開講：前者重新整理就好，後者得先去那個專案移除。
            // 併成一句的話，使用者不知道下一步該做什麼。
            if (meeting.ProjectId == projectId)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄已經屬於這個專案，請重新整理後再試。");
            }

            if (meeting.ProjectId is not null)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄已歸屬其他專案，請先從該專案移除後再重新歸屬。");
            }

            // ⚠️ 這裡擋的理由和 DetachFromProjectAsync 不同，不是為了避免資料不一致。
            // job runner 全程沒碰過 ProjectId，EF 對 tracked entity 只送有變更的欄位，
            // 所以並行的歸屬不會被蓋掉。擋的是品質語意：runner 在「開始生成」時就用當下的
            // ProjectId 決定要套哪一份常用名詞，生成途中歸屬過去的草稿其實沒套到該專案的
            // 名詞校正，卻會躺在那個專案的歷史清單裡，看起來像套過了。
            if (meeting.DraftStatus is DraftStatus.Processing or DraftStatus.Pending)
            {
                return VerifyRecordResultFactory.Build(false, "這筆逐字稿正在產生會議紀錄，請等結束後再歸屬。");
            }

            var project = await context.Project.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
            }

            // 只寫歸屬。草稿內容、提示詞快照、與會者快照、生成時間全部原封不動，
            // 也不重新入列——這支的存在理由就是「不必重跑、不必再付一次 API 費用」。
            meeting.ProjectId = projectId;
            meeting.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation(
                "Meeting attached to project. MeetingId={MeetingId}, Title={Title}, ProjectId={ProjectId}",
                meetingId,
                meeting.Title,
                projectId);

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to attach meeting to project. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "歸屬會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 把一份逐字稿從專案移除：清掉歸屬與 AI 草稿，讓它回到「可選逐字稿」清單。
    ///
    /// <para>
    /// 存在的理由是「生成錯專案」沒有出口——<see cref="RequestDraftAsync"/> 會擋掉已歸屬
    /// 其他專案的逐字稿，而 <see cref="DeleteAsync"/> 連影音檔與逐字稿一起刪，等於要重新上傳
    /// 並<b>重新付一次轉錄費用</b>。這支只解除歸屬，實體檔案一律保留。
    /// </para>
    ///
    /// <para>
    /// <b>不動的東西</b>：影音檔、逐字稿檔、這場會議的 AI 問答對話（它綁的是會議不是專案），
    /// 以及已經抽出來的待辦（待辦有自己的 <c>ProjectId</c>，是獨立的工作項目）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 這支<b>不是</b> <see cref="AttachToProjectAsync"/> 的可逆反向操作：它會連已產生的
    /// 草稿一起刪掉，再歸屬回去也救不回來。
    /// </para>
    /// </summary>
    /// <param name="projectId">預期的目前歸屬專案。傳進來是為了擋掉「畫面過期、實際上已經被移到別的專案」。</param>
    public async Task<VerifyRecordResult> DetachFromProjectAsync(
        int meetingId,
        int projectId,
        CancellationToken cancellationToken = default)
    {
        Logger.LogInformation(
            "Detaching meeting from project. MeetingId={MeetingId}, ProjectId={ProjectId}", meetingId, projectId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要移除的會議紀錄。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Detach denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限移除這筆會議紀錄。");
            }

            if (meeting.ProjectId != projectId)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄已經不屬於這個專案，請重新整理後再試。");
            }

            // 背景工作正在跑（或排隊中）時不能解除歸屬：job runner 只吃 meetingId，
            // 它會在結束時把草稿寫回這筆紀錄，變成「已移除卻又冒出一份草稿」。
            if (meeting.DraftStatus is DraftStatus.Processing or DraftStatus.Pending)
            {
                return VerifyRecordResultFactory.Build(false, "這筆逐字稿正在產生會議紀錄，請等結束後再移除。");
            }

            meeting.ProjectId = null;
            meeting.DraftContent = null;
            meeting.DraftStatus = DraftStatus.NotGenerated;
            meeting.DraftError = null;
            meeting.DraftPromptTemplateId = null;
            meeting.DraftPromptTemplateName = null;
            meeting.DraftAttendees = null;
            meeting.DraftStartedAt = null;
            meeting.DraftCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation(
                "Meeting detached from project. MeetingId={MeetingId}, Title={Title}", meetingId, meeting.Title);

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to detach meeting from project. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "移除會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 把會議排入草稿生成佇列，並視情況歸屬到指定專案。
    ///
    /// 一份逐字稿只能屬於一個專案：已被「其他」專案取用的會拒絕，
    /// 但屬於同一個專案的可以重跑（換提示詞重新生成，會覆蓋既有草稿）。
    /// </summary>
    /// <param name="projectId">
    /// 要歸屬的專案；<c>null</c> 代表「不指定」而<b>不是</b>「解除歸屬」。四種情形：
    /// <list type="bullet">
    /// <item>未歸屬 ＋ null → 放行，維持未歸屬（不必先建專案也能產生會議紀錄）</item>
    /// <item>未歸屬 ＋ 專案 A → 放行，歸屬到 A</item>
    /// <item>已屬 A ＋ null → 放行，<b>保留</b>原歸屬 A</item>
    /// <item>已屬 A ＋ 專案 B → 拒絕</item>
    /// </list>
    /// 解除歸屬是破壞性動作，唯一的出口是 <see cref="DetachFromProjectAsync"/>；
    /// 讓這支在沒指定專案時順手解除，等於同一個參數兩種語意，而且是靜默的。
    /// </param>
    /// <param name="attendees">
    /// 本次實際與會者（由畫面從專案名冊勾選）。刻意<b>不</b>驗證是否真的在名冊內——
    /// 名單只是給模型的提示，加驗證只會引入「使用者開著頁面時別人改了名冊、送出被整筆退回」這種假失敗。
    /// </param>
    public async Task<VerifyRecordResult> RequestDraftAsync(
        int meetingId,
        int? projectId,
        int promptTemplateId,
        IEnumerable<string>? attendees = null,
        CancellationToken cancellationToken = default)
    {
        Logger.LogInformation(
            "Requesting meeting draft. MeetingId={MeetingId}, ProjectId={ProjectId}, PromptTemplateId={PromptTemplateId}",
            meetingId,
            projectId,
            promptTemplateId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要產生會議紀錄的逐字稿。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Draft request denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限對這筆逐字稿產生會議紀錄。");
            }

            if (meeting.TranscriptionStatus != TranscriptionStatus.Completed
                || string.IsNullOrWhiteSpace(meeting.TranscriptRelativePath))
            {
                return VerifyRecordResultFactory.Build(false, "這筆逐字稿尚未轉錄完成，無法產生會議紀錄。");
            }

            // 同轉錄：Pending 也要擋，否則同一筆逐字稿會入列兩次、生成兩趟並重複計費。
            if (meeting.DraftStatus is DraftStatus.Processing or DraftStatus.Pending)
            {
                return VerifyRecordResultFactory.Build(false, "這筆逐字稿已排入產生會議紀錄，請稍候。");
            }

            // ⚠️ projectId is not null 這個條件不能省：少了它，「已歸屬 A 但這次沒指定專案」
            // 會被 A != null 判成衝突而拒絕。編譯器對這個錯誤不會有任何警告。
            if (projectId is not null && meeting.ProjectId is not null && meeting.ProjectId != projectId)
            {
                return VerifyRecordResultFactory.Build(false, "這份逐字稿已歸屬於其他專案，無法重複取用。");
            }

            var effectiveProjectId = projectId ?? meeting.ProjectId;

            // ⚠️ 這個 if 同樣不能省：x.Id == effectiveProjectId 在 null 時是 lifted comparison、
            // 恆為 false，查不到任何列就會讓「不指定專案」一律回「找不到指定的專案項目」。
            if (effectiveProjectId is not null)
            {
                var project = await context.Project.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == effectiveProjectId, cancellationToken);
                if (project is null)
                {
                    return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
                }
            }

            var template = await context.PromptTemplate.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == promptTemplateId, cancellationToken);
            if (template is null || !template.IsEnabled)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的提示詞，或該提示詞已停用。");
            }

            if (!TagStringHelper.IsTeamAccessible(template.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Draft request denied by prompt template team scope. PromptTemplateId={PromptTemplateId}", promptTemplateId);
                return VerifyRecordResultFactory.Build(false, "沒有權限使用指定的提示詞。");
            }

            meeting.ProjectId = effectiveProjectId;
            meeting.DraftPromptTemplateId = template.Id;
            meeting.DraftPromptTemplateName = template.Name;

            // ToStored 已負責 trim／去空／忽略大小寫去重／保順序／全空回 null。
            // Take 的唯一理由是界定提示詞大小，不是權限。
            meeting.DraftAttendees = TagStringHelper.ToStored((attendees ?? []).Take(MaxDraftAttendees));
            meeting.DraftStatus = DraftStatus.Pending;
            meeting.DraftError = null;
            meeting.DraftStartedAt = null;
            meeting.DraftCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            // 先登錄進度再入列，理由同轉錄：背景工作要等輪到才知道，先登錄畫面才立刻看得到「排隊中」。
            draftProgressNotifier.Enqueued(meetingId, meeting.Title, meeting.Teams);
            await draftQueue.EnqueueAsync(BuildJobRequest(meetingId), cancellationToken);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to request meeting draft. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "排入會議紀錄生成失敗。", ex);
        }
    }

    /// <summary>
    /// 取得可供「AI 轉會議紀錄」選用的逐字稿（轉錄已完成者）。
    /// 一併回傳歸屬資訊，讓畫面把已被其他專案取用的項目標示為不可選。
    /// </summary>
    public async Task<List<MeetingAdapterModel>> GetSelectableTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking()
            .Where(x => x.TranscriptionStatus == TranscriptionStatus.Completed);

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Meeting>(x => x.Teams, scope.Teams));
        }

        var items = await dataSource
            .Include(x => x.Project)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return MapWithProjectTitle(items);
    }

    /// <summary>取得某專案底下的會議紀錄（歷史清單）。</summary>
    public async Task<List<MeetingAdapterModel>> GetByProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking()
            .Where(x => x.ProjectId == projectId);

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Meeting>(x => x.Teams, scope.Teams));
        }

        var items = await dataSource
            .Include(x => x.Project)
            .OrderByDescending(x => x.DraftCompletedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return MapWithProjectTitle(items);
    }

    /// <summary>人工編修 AI 產生的會議紀錄草稿。</summary>
    public async Task<VerifyRecordResult> UpdateDraftAsync(
        int meetingId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要修改的會議紀錄。");
            }

            var scope = await accessScope.GetAsync();
            if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Draft update denied by team scope. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆會議紀錄。");
            }

            if (meeting.DraftStatus == DraftStatus.Processing)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄正在重新產生中，請稍候再編修。");
            }

            meeting.DraftContent = content;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation("Meeting draft updated. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update meeting draft. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "修改會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 對應成 AdapterModel 並補上專案名稱。
    /// ProjectTitle 是跨物件欄位，AutoMapper 的同名慣例對應不到，因此在這裡填。
    /// </summary>
    private List<MeetingAdapterModel> MapWithProjectTitle(List<Meeting> items)
    {
        var result = new List<MeetingAdapterModel>();
        foreach (var item in items)
        {
            var model = Mapper.Map<MeetingAdapterModel>(item);
            model.ProjectTitle = item.Project?.Title;
            result.Add(model);
        }

        return result;
    }

    #endregion
}
