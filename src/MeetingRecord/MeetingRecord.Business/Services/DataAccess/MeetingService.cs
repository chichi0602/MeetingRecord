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
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 會議紀錄的服務層（頁面經 API 與對外 API 共用）。CRUD 骨架比照 <see cref="PromptTemplateService"/>；
/// 資料權限跟著專案的主責與協作團隊走（<see cref="ProjectAccessService"/>，0.4.102），取代原本的團隊標籤；
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
    private readonly ProjectAccessService projectAccess;
    private readonly MeetingFileStore fileStore;
    private readonly AiChatStore chatStore;
    private readonly ITranscriptionQueue transcriptionQueue;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private readonly IMeetingDraftQueue draftQueue;
    private readonly IMeetingDraftProgressNotifier draftProgressNotifier;
    private readonly CurrentUserService currentUserService;
    private readonly AiCallLogService aiCallLogService;
    private readonly IAiQuotaService quotaService;
    private readonly IAuditLogService auditLogService;
    private readonly IJobCancellationRegistry cancellationRegistry;
    private readonly IAiCallerProvider? callerProvider;

    /// <summary>
    /// 使用者取消上傳時 <see cref="SaveMediaAsync"/> 回傳的訊息（1.0.37）。
    /// 頁面靠它辨識「取消」：從右下角面板取消時，頁面自己的取消來源不會被觸發。
    /// </summary>
    public const string UploadCancelledMessage = "已取消上傳。";

    /// <summary>取消上傳且已依要求連會議一起刪除時的訊息（1.0.37）。刪除失敗時回 <see cref="UploadCancelledMessage"/>。</summary>
    public const string UploadCancelledAndMeetingDeletedMessage = "已取消上傳，這筆會議紀錄已一併移除。";

    public IMapper Mapper { get; }
    public ILogger<MeetingService> Logger { get; }

    public MeetingService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MeetingService> logger,
        ProjectAccessService projectAccess,
        MeetingFileStore fileStore,
        AiChatStore chatStore,
        ITranscriptionQueue transcriptionQueue,
        ITranscriptionProgressNotifier progressNotifier,
        IMeetingDraftQueue draftQueue,
        IMeetingDraftProgressNotifier draftProgressNotifier,
        CurrentUserService currentUserService,
        AiCallLogService aiCallLogService,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        IJobCancellationRegistry cancellationRegistry,
        IAiCallerProvider? callerProvider = null)
    {
        this.callerProvider = callerProvider;
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.projectAccess = projectAccess;
        this.fileStore = fileStore;
        this.chatStore = chatStore;
        this.transcriptionQueue = transcriptionQueue;
        this.progressNotifier = progressNotifier;
        this.draftQueue = draftQueue;
        this.draftProgressNotifier = draftProgressNotifier;
        this.currentUserService = currentUserService;
        this.aiCallLogService = aiCallLogService;
        this.quotaService = quotaService;
        this.auditLogService = auditLogService;
        this.cancellationRegistry = cancellationRegistry;
    }

    /// <summary>
    /// 組出背景工作的請求，把「是誰按的」一起帶進佇列。
    ///
    /// <para>
    /// ⚠️ 必須在這裡取使用者。這支跑在使用者的 API 請求裡（或已填入上傳者的上傳背景工作裡），拿得到觸發者；
    /// 但轉錄／生成背景服務為每筆工作自建的 scope 裡 CurrentUserService 是空白物件（Id=0），
    /// 等到 runner 才去問，用量帳本的「誰」就永遠是空的。
    /// 1.1.28：經 <see cref="IAiCallerProvider"/> 取，API 請求從 claims 認得觸發者。
    /// </para>
    /// </summary>
    private MeetingJobRequest BuildJobRequest(int meetingId)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser, callerProvider);

        return new MeetingJobRequest(meetingId, userId, userName);
    }

    /// <summary>1.1.71：留一份目前的會議紀錄（經 <see cref="MeetingDraftVersionRecorder"/>），記下是誰造成這次覆蓋。</summary>
    private Task<bool> SnapshotDraftAsync(Meeting meeting, string reason, CancellationToken cancellationToken)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser, callerProvider);
        return MeetingDraftVersionRecorder.SnapshotAsync(context, meeting, reason, userId, userName, cancellationToken);
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
                (x.MediaOriginalFileName ?? string.Empty).Contains(search) ||
                // 1.1.69：也搜會議紀錄內容（逐字稿在磁碟上，不能用 SQL 搜）。
                (x.DraftContent ?? string.Empty).Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Meeting>(x => x.Categories, dataRequest.CategoryFilters));
        }

        dataSource = (await projectAccess.GetAsync()).Filter(dataSource);

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
        // Take 刻意不看 dataRequest.Take：清單頁一律傳 0，原本包在 if (Take != 0) 裡等於從來沒有分頁（1.0.57 修正）。
        dataSource = dataSource
            .Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize)
            .Take(dataRequest.PageSize);

        List<Meeting> records = await dataSource.ToListAsync();
        result.Result = MapWithProjectTitle(records);
        Logger.LogDebug("Loaded meetings successfully. Count={Count}", result.Count);
        return result;
    }

    /// <summary>
    /// 對外 API 的會議搜尋（<c>POST api/Meeting/search</c>，1.1.20）：
    /// 關鍵字比對標題、描述、影音檔名；可依轉錄狀態篩選；預設依更新時間新到舊。
    /// 畫面清單（分類篩選、畫面欄位排序）是 <see cref="GetAsync(DataRequest)"/>。
    /// </summary>
    public async Task<(List<MeetingAdapterModel> Items, int TotalCount)> SearchFlatAsync(MeetingRecord.Dtos.Commons.MeetingSearchRequestDto request)
    {
        var query = (await projectAccess.GetAsync()).Filter(context.Meeting.AsNoTracking()).Include(x => x.Project).AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Title.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)) ||
                (x.MediaOriginalFileName != null && x.MediaOriginalFileName.Contains(request.Keyword)) ||
                (x.DraftContent != null && x.DraftContent.Contains(request.Keyword)));
        }

        if (request.TranscriptionStatus.HasValue)
        {
            var status = (TranscriptionStatus)request.TranscriptionStatus.Value;
            query = query.Where(x => x.TranscriptionStatus == status);
        }

        query = request.SortBy?.ToLower() switch
        {
            "title" => request.SortDescending ? query.OrderByDescending(x => x.Title) : query.OrderBy(x => x.Title),
            "meetingdate" => request.SortDescending ? query.OrderByDescending(x => x.MeetingDate) : query.OrderBy(x => x.MeetingDate),
            "transcriptionstatus" => request.SortDescending ? query.OrderByDescending(x => x.TranscriptionStatus) : query.OrderBy(x => x.TranscriptionStatus),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();
        return (MapWithProjectTitle(items), totalCount);
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

        var access = await projectAccess.GetAsync();
        if (!access.CanViewMeeting(item.ProjectId, item.CreatedByUserId))
        {
            Logger.LogWarning("Meeting access denied by project access. MeetingId={MeetingId}", id);
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
    /// <param name="via">入口（稽核用，1.1.20）：API 傳 <see cref="AuditVia.Api"/>。</param>
    public async Task<VerifyRecordResult> AddAsync(MeetingAdapterModel paraObject, string via = AuditVia.Web)
    {
        Logger.LogInformation("Creating meeting. Title={Title}", paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting itemParameter = Mapper.Map<Meeting>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            // 上傳者（0.4.99）：未歸屬專案之前，只有他和管理者看得到這筆會議。
            var access = await projectAccess.GetAsync();
            itemParameter.CreatedByUserId = access.UserId == 0 ? null : access.UserId;

            await context.Meeting.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            paraObject.Id = itemParameter.Id;

            await AuditAsync(AuditActions.MeetingCreate, itemParameter.Id, $"title={itemParameter.Title}; via={via}");
            Logger.LogInformation("Meeting created successfully. MeetingId={MeetingId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to create meeting. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 修改會議紀錄。影音檔與轉錄相關欄位一律沿用資料庫既有值，
    /// 避免畫面上的舊複本把背景轉錄剛寫入的狀態蓋掉。
    /// </summary>
    /// <param name="via">入口（稽核用，1.1.20）：API 傳 <see cref="AuditVia.Api"/>。</param>
    public async Task<VerifyRecordResult> UpdateAsync(MeetingAdapterModel paraObject, string via = AuditVia.Web)
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

            if (!(await projectAccess.GetAsync()).CanViewMeeting(item.ProjectId, item.CreatedByUserId))
            {
                Logger.LogWarning("Meeting update denied by project access. MeetingId={MeetingId}", paraObject.Id);
                await AuditDataScopeDeniedAsync(nameof(Meeting), paraObject.Id, "Update");
                return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆會議紀錄。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            Meeting itemData = Mapper.Map<Meeting>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            itemData.MediaOriginalFileName = item.MediaOriginalFileName;
            itemData.MediaStoredFileName = item.MediaStoredFileName;
            itemData.MediaRelativePath = item.MediaRelativePath;
            itemData.MediaContentType = item.MediaContentType;
            itemData.MediaFileSize = item.MediaFileSize;
            itemData.MediaDurationSeconds = item.MediaDurationSeconds;
            itemData.TranscriptRelativePath = item.TranscriptRelativePath;
            itemData.TranscriptionStatus = item.TranscriptionStatus;
            itemData.TranscriptionError = item.TranscriptionError;
            itemData.TranscriptionStartedAt = item.TranscriptionStartedAt;
            itemData.TranscriptionCompletedAt = item.TranscriptionCompletedAt;
            itemData.TranscriptionEngine = item.TranscriptionEngine;
            itemData.TranscriptionDiarize = item.TranscriptionDiarize;
            itemData.TranscriptionMaxSpeakers = item.TranscriptionMaxSpeakers;
            itemData.TranscriptionElapsedSeconds = item.TranscriptionElapsedSeconds;

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

            // 1.1.69：同理，這三個在 AdapterModel 上沒有（或不該由表單改），不沿用就會被清掉。
            // SpeakerNames 是 1.1.64 漏掉的——改個標題就把標示好的講者名字清光。
            itemData.SpeakerNames = item.SpeakerNames;
            itemData.SpeakerClips = item.SpeakerClips;
            itemData.AutoDraftPromptTemplateId = item.AutoDraftPromptTemplateId;
            itemData.AwaitingSpeakerNames = item.AwaitingSpeakerNames;

            // ProjectId 從 0.4.73 起也一併沿用。歸屬的權威路徑已經是
            // AttachToProjectAsync／DetachFromProjectAsync 兩支方法，不再是這張表單；
            // 讓畫面上的舊複本寫回去，只會在別處剛改過歸屬時把它靜默退回舊值。
            itemData.ProjectId = item.ProjectId;
            itemData.CreatedByUserId = item.CreatedByUserId;

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            await AuditAsync(AuditActions.MeetingUpdate, itemData.Id, $"title={itemData.Title}; via={via}");
            Logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", itemData.Id, itemData.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to update meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改會議紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除會議紀錄，並移除影音檔與逐字稿的實體檔案。
    /// 實體檔案刪除失敗只記 Warning，不阻斷資料列刪除（見 docs/features/檔案上傳機制.md §4）。
    /// </summary>
    /// <param name="via">入口（1.0.34 稽核用）：API 的刪除也走這裡，Controller 傳 <see cref="AuditVia.Api"/>。</param>
    public async Task<VerifyRecordResult> DeleteAsync(int id, string via = AuditVia.Web)
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

            if (!(await projectAccess.GetAsync()).CanViewMeeting(item.ProjectId, item.CreatedByUserId))
            {
                Logger.LogWarning("Meeting deletion denied by project access. MeetingId={MeetingId}", id);
                await AuditDataScopeDeniedAsync(nameof(Meeting), id, "Delete");
                return VerifyRecordResultFactory.Build(false, "沒有權限刪除這筆會議紀錄。");
            }

            // 1.0.47：同專案的人可以看、可以編修，但刪除只限上傳者與管理者；
            // 1.0.52 起專案負責人也可以刪專案底下任何人的會議。
            var deleteAccess = await projectAccess.GetAsync();
            var isProjectOwner = item.ProjectId is { } projectId && deleteAccess.IsProjectOwner(projectId);
            if (!deleteAccess.IsMeetingOwner(item.CreatedByUserId) && !isProjectOwner)
            {
                return VerifyRecordResultFactory.Build(false, DeleteDeniedMessage);
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, item.ProjectId, await projectAccess.GetAsync()) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            // 背景工作還在跑就不能刪（0.4.115）：工作不會因此停下，跑完寫出的逐字稿檔
            // 找不到資料列可以掛，就永遠留在磁碟上；轉錄也會繼續計費。
            if (IsJobActive(item))
            {
                Logger.LogWarning("Meeting deletion rejected because a background job is active. MeetingId={MeetingId}", id);
                return VerifyRecordResultFactory.Build(false, "這筆會議正在轉錄或產生會議紀錄，請等它完成，或從右下角的進度面板取消後再刪除。");
            }

            var mediaRelativePath = item.MediaRelativePath;
            var transcriptRelativePath = item.TranscriptRelativePath;
            var speakerClips = TranscriptSpeakerLabels.ParseNames(item.SpeakerClips).Values.ToList();

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            fileStore.TryDeleteMedia(mediaRelativePath);
            fileStore.TryDeleteTranscript(transcriptRelativePath);
            fileStore.TryDeleteSpeakerClips(speakerClips);

            // 0.4.60 起對話存在檔案系統，資料表已移除，Cascade 不會再幫我們清掉它。
            chatStore.TryDelete(AiChatScope.Meeting, id);

            // 1.0.29：AI 對話紀錄含這場會議的逐字稿與會議紀錄全文，跟著會議一起刪（用量帳本不動）。
            await TryDeleteAiCallLogsAsync(id);

            await AuditAsync(AuditActions.MeetingDelete, id, $"title={item.Title}; via={via}");
            Logger.LogInformation("Meeting deleted successfully. MeetingId={MeetingId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to delete meeting. MeetingId={MeetingId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除會議紀錄失敗。", ex);
        }
    }

    #endregion

    // ───────── 稽核（1.0.34）：操作者由 WriteForCurrentUserAsync 決定，網頁與 API 都認得出是誰 ─────────

    private Task AuditAsync(string action, int meetingId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: nameof(Meeting), targetId: meetingId.ToString(), detail: detail);

    /// <summary>資料範圍擋下的寫入：看不到的會議，或歸屬／產生草稿時指定了看不到的專案。</summary>
    private Task AuditDataScopeDeniedAsync(string targetType, int targetId, string operation)
        => auditLogService.WriteForCurrentUserAsync(
            AuditActions.PermissionDenied, success: false, targetType: targetType, targetId: targetId.ToString(),
            detail: $"reason=DataScope; op={operation}");

    /// <summary>刪除會議的 AI 對話紀錄。盡力而為：失敗只記日誌，不影響已完成的刪除。</summary>
    private async Task TryDeleteAiCallLogsAsync(int meetingId)
    {
        try
        {
            var removed = await aiCallLogService.DeleteByMeetingAsync(meetingId);
            if (removed > 0)
            {
                Logger.LogInformation("AI call logs deleted with meeting. MeetingId={MeetingId}, Rows={Rows}", meetingId, removed);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete AI call logs of meeting. MeetingId={MeetingId}", meetingId);
        }
    }

    /// <summary>
    /// 入列前的用量上限預檢（1.0.29）：已達上限就直接回訊息，不改任何狀態也不入列。
    /// 背景工作開始時還會再檢查一次（上傳後自動入列的轉錄就靠那一次）。
    /// </summary>
    private async Task<string?> CheckQuotaAsync(CancellationToken cancellationToken)
    {
        var (userId, _) = AiUsageAttribution.Resolve(currentUserService.CurrentUser, callerProvider);
        var blocked = await quotaService.CheckAsync(userId, cancellationToken);
        return blocked is null ? null : AiQuotaExceededException.BuildMessage(blocked);
    }

    /// <summary>轉錄或會議紀錄生成正在排隊或執行中。</summary>
    private static bool IsJobActive(Meeting meeting)
        => meeting.TranscriptionStatus is TranscriptionStatus.Pending or TranscriptionStatus.Processing
           || meeting.DraftStatus is DraftStatus.Pending or DraftStatus.Processing;

    /// <summary>上傳者專屬動作被擋下時的訊息（1.0.47）。</summary>
    public static string OwnerOnlyMessage(string action) => $"只有上傳者或管理者可以{action}。";

    /// <summary>刪除會議被擋下時的訊息（1.0.52：上傳者或專案負責人）。</summary>
    public const string DeleteDeniedMessage = "只有上傳者或專案負責人可以刪除這筆會議紀錄。";

    /// <summary>
    /// 從這場會議抽出、還連著它的待辦筆數（1.0.47，刪除確認框用）。刪會議時這些待辦會保留，只是不再連到會議。
    /// 只算看得到的會議；看不到的回 0。
    /// </summary>
    public async Task<int> CountLinkedTodosAsync(int meetingId, CancellationToken cancellationToken = default)
    {
        var access = await projectAccess.GetAsync();
        var meetings = access.Filter(context.Meeting.AsNoTracking().Where(x => x.Id == meetingId));
        if (!await meetings.AnyAsync(cancellationToken))
        {
            return 0;
        }

        return await context.Todo.AsNoTracking().CountAsync(x => x.MeetingId == meetingId, cancellationToken);
    }

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
    /// <paramref name="deleteMeetingOnCancel"/>：使用者取消上傳時連會議一起刪（1.0.37）——
    /// 只給「這次存檔才新增出來」的會議用；修改既有會議時取消，會議與舊檔都要留著。
    /// </summary>
    public async Task<VerifyRecordResult> SaveMediaAsync(
        int meetingId,
        MeetingMediaUploadInput uploadFile,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        bool deleteMeetingOnCancel = false)
    {
        Logger.LogInformation(
            "Uploading meeting media. MeetingId={MeetingId}, FileName={FileName}, FileSize={FileSize}",
            meetingId,
            uploadFile.FileName,
            uploadFile.FileSize);

        if (CheckMediaFile(uploadFile.FileName, uploadFile.FileSize) is { } fileError)
        {
            return fileError;
        }

        StoredMediaFile? stored = null;
        var committed = false;
        var uploadStarted = false;
        var cancelledFromPanel = false;
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (await CheckMeetingForMediaUploadAsync(meeting, meetingId) is { } meetingError)
            {
                return meetingError;
            }

            var previousMediaRelativePath = meeting.MediaRelativePath;
            var previousTranscriptRelativePath = meeting.TranscriptRelativePath;
            var previousSpeakerClips = TranscriptSpeakerLabels.ParseNames(meeting.SpeakerClips).Values.ToList();

            // 上傳進度也寫進共用通知器（0.4.117）：同一個帳號的其他頁籤、瀏覽器、電腦，
            // 以及看得到這筆會議的其他人，右下角面板與清單都看得到「上傳中 xx%」。
            progressNotifier.StartUpload(meetingId, meeting.Title, meeting.ProjectId, meeting.CreatedByUserId);
            uploadStarted = true;
            var sharedProgress = new ForwardingProgress(percent =>
            {
                progressNotifier.ReportUpload(meetingId, percent);
                progress?.Report(percent);
            });

            // 寫檔期間登記進取消登記處（1.0.37），右下角面板的「取消」才中斷得到；寫完就撤銷登記，
            // 理由同下面 SaveChangesAsync 那段：取消只作用在寫檔。
            using (var uploadHandle = cancellationRegistry.BeginJob(BackgroundJobKind.Upload, meetingId, cancellationToken))
            {
                try
                {
                    stored = await fileStore.SaveMediaAsync(meeting.CreatedAt, uploadFile, sharedProgress, uploadHandle.Token);
                }
                finally
                {
                    cancelledFromPanel = uploadHandle.IsCancelledByUser;
                }
            }

            meeting.MediaOriginalFileName = stored.OriginalFileName;
            meeting.MediaStoredFileName = stored.StoredFileName;
            meeting.MediaRelativePath = stored.RelativePath;
            meeting.MediaContentType = stored.ContentType;
            meeting.MediaFileSize = stored.FileSize;
            meeting.MediaDurationSeconds = null;
            meeting.TranscriptRelativePath = null;
            meeting.SpeakerClips = null;
            meeting.TranscriptionStatus = TranscriptionStatus.Pending;
            meeting.TranscriptionError = null;
            meeting.TranscriptionStartedAt = null;
            meeting.TranscriptionCompletedAt = null;
            meeting.TranscriptionElapsedSeconds = null;

            // 1.1.73：這次上傳選的轉錄方式（建立上傳工作階段時已驗證）。
            meeting.TranscriptionEngine = uploadFile.TranscriptionEngine;
            meeting.TranscriptionDiarize = uploadFile.TranscriptionDiarize;
            meeting.TranscriptionMaxSpeakers = uploadFile.TranscriptionMaxSpeakers;

            // 會議日期沒填就帶入上傳當天；已填的不覆蓋，要更正仍可從畫面編輯。
            meeting.MeetingDate ??= DateTime.Today;

            // 1.1.69：轉錄完自動產生會議紀錄。每次上傳都重設（新檔案是新的一趟）；提示詞不存在或停用就當成不自動產生。
            meeting.AwaitingSpeakerNames = false;
            meeting.AutoDraftPromptTemplateId = uploadFile.AutoDraftPromptTemplateId is { } autoTemplateId
                && await context.PromptTemplate.AsNoTracking().AnyAsync(x => x.Id == autoTemplateId && x.IsEnabled, CancellationToken.None)
                    ? autoTemplateId
                    : null;

            meeting.UpdatedAt = DateTime.Now;

            // ⚠️ 取消只作用在上面的寫檔（使用者按「取消上傳」，1.0.25）。檔案寫完就不再理會取消：
            //    存檔後才取消會留下「已存檔、待處理」卻沒入列的會議，永遠不會被轉錄。
            await context.SaveChangesAsync(CancellationToken.None);
            CleanTrackingHelper.Clean<Meeting>(context);
            committed = true;

            // 新檔案登錄成功後才清掉舊的影音檔與逐字稿。
            fileStore.TryDeleteMedia(previousMediaRelativePath);
            fileStore.TryDeleteTranscript(previousTranscriptRelativePath);
            fileStore.TryDeleteSpeakerClips(previousSpeakerClips);

            // 先登錄進度再入列：背景工作要等輪到才會知道這件事，先登錄畫面才立刻看得到「排隊中」。
            // 1.1.68：入列前清掉這筆殘留的取消標記（工作剛結束時按取消會留下），否則這一趟會被無聲取消。
            // 一定要在登錄進度之前：登錄後面板才看得到、才按得到取消，那之後留下的標記才是有效的。
            cancellationRegistry.ClearPendingCancel(BackgroundJobKind.Transcription, meetingId);
            progressNotifier.Enqueued(meetingId, meeting.Title, meeting.ProjectId, meeting.CreatedByUserId);
            await transcriptionQueue.EnqueueAsync(BuildJobRequest(meetingId), CancellationToken.None);

            await AuditAsync(AuditActions.MeetingMediaUpload, meetingId, $"file={stored.OriginalFileName}; size={stored.FileSize}");
            Logger.LogInformation("Meeting media uploaded and queued for transcription. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            // ⚠️ 大檔上傳中途 scope 被結束時，DbContext 已隨之釋放，Clear 會再丟 ObjectDisposedException，
            //    蓋掉真正的錯誤、也跳過下面的紀錄與孤兒檔清理（0.4.116）。scope 都沒了，也沒有追蹤要清。
            try
            {
                context.ChangeTracker.Clear();
            }
            catch (ObjectDisposedException)
            {
            }

            // 使用者按「取消上傳」不是錯誤，記 Information 就好（1.0.25）。
            // 不要求例外一定是 OperationCanceledException（1.0.37）：瀏覽器檔案串流被取消時不保證丟這個型別，
            // 只要使用者按過取消（本頁或右下角面板），之後的中斷一律當取消。
            var cancelledByUser = cancellationToken.IsCancellationRequested || cancelledFromPanel;
            if (cancelledByUser)
            {
                Logger.LogInformation("Meeting media upload cancelled by user. MeetingId={MeetingId}", meetingId);
            }
            else
            {
                Logger.LogError(ex, "Failed to upload meeting media. MeetingId={MeetingId}", meetingId);
            }

            // 資料庫沒寫進去，剛落地的新檔就沒有任何資料列指向它（0.4.115）。
            if (!committed && stored is not null)
            {
                fileStore.TryDeleteMedia(stored.RelativePath);
            }

            // 新增時就帶著檔案、上傳被取消的會議，連資料列一起刪（1.0.37，使用者要求）。
            // 刪掉了就把通知器那筆也移掉，不留一列指向不存在會議的「失敗」。
            if (cancelledByUser && deleteMeetingOnCancel && uploadStarted && !committed)
            {
                var deleteResult = await DeleteAsync(meetingId);
                if (deleteResult.Success)
                {
                    progressNotifier.Dismiss(meetingId);
                    return VerifyRecordResultFactory.Build(false, UploadCancelledAndMeetingDeletedMessage);
                }

                Logger.LogWarning(
                    "Meeting kept after upload cancellation because deletion failed. MeetingId={MeetingId}, Reason={Reason}",
                    meetingId,
                    deleteResult.Message);
            }

            // 瀏覽器端放棄（登入失效、重試用完）或逾時，原因要帶給使用者（1.0.67）：
            // 以前一律「影音檔上傳失敗」，登入過期的人不知道要重新登入。其他例外的訊息是內部細節，不秀出來。
            var interrupted = ex as UploadInterruptedException ?? ex.InnerException as UploadInterruptedException;
            var failureMessage = interrupted is null ? null : $"影音檔上傳失敗：{interrupted.Message}";

            // 其他視窗正看著「上傳中 xx%」，不標成失敗就會永遠卡在那個百分比（0.4.117）。
            if (uploadStarted && !committed)
            {
                progressNotifier.ReportFailed(meetingId, cancelledByUser ? UploadCancelledMessage : failureMessage ?? "影音檔上傳失敗，請重新上傳。");
            }
            return cancelledByUser
                ? VerifyRecordResultFactory.Build(false, UploadCancelledMessage)
                : VerifyRecordResultFactory.Build(false, failureMessage ?? "影音檔上傳失敗。", ex);
        }
    }

    /// <summary>
    /// 上傳影音檔前的檢查（1.1.16，從 <see cref="SaveMediaAsync"/> 抽出來）：檔名格式、大小、會議存在、資料範圍、只限上傳者、
    /// 封存唯讀、背景工作進行中、另一個視窗正在上傳。WASM 頁經 API 建立上傳工作階段前先跑，不合就直接回原因，
    /// 不必等瀏覽器送完第一塊才知道；真正存檔時 <see cref="SaveMediaAsync"/> 會再檢查一次。
    /// </summary>
    public async Task<VerifyRecordResult> CheckMediaUploadAsync(int meetingId, string fileName, long fileSize)
    {
        if (CheckMediaFile(fileName, fileSize) is { } fileError)
        {
            return fileError;
        }

        var meeting = await context.Meeting.AsNoTracking().FirstOrDefaultAsync(x => x.Id == meetingId);
        return await CheckMeetingForMediaUploadAsync(meeting, meetingId) ?? VerifyRecordResultFactory.Build(true);
    }

    private static VerifyRecordResult? CheckMediaFile(string fileName, long fileSize)
    {
        if (!MeetingMediaPolicy.IsAllowedFileName(fileName))
        {
            return VerifyRecordResultFactory.Build(false, $"不支援的檔案格式，允許的格式：{MeetingMediaPolicy.AllowedExtensionsText}。");
        }

        if (fileSize > MeetingMediaPolicy.MaxUploadFileSize)
        {
            return VerifyRecordResultFactory.Build(
                false,
                $"影音檔大小不可超過 {MeetingMediaPolicy.FormatFileSize(MeetingMediaPolicy.MaxUploadFileSize)}。");
        }

        return null;
    }

    /// <summary>會議層級的上傳檢查；通過回 null。</summary>
    private async Task<VerifyRecordResult?> CheckMeetingForMediaUploadAsync(Meeting? meeting, int meetingId)
    {
        if (meeting is null)
        {
            Logger.LogWarning("Meeting media upload rejected because record was not found. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "找不到要上傳影音檔的會議紀錄。");
        }

        var access = await projectAccess.GetAsync();
        if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
        {
            Logger.LogWarning("Meeting media upload denied by project access. MeetingId={MeetingId}", meetingId);
            await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "MediaUpload");
            return VerifyRecordResultFactory.Build(false, "沒有權限對這筆會議紀錄上傳影音檔。");
        }

        // 1.0.47：換影音檔只限上傳者與管理者（新增會議時上傳者就是自己）。
        if (!access.IsMeetingOwner(meeting.CreatedByUserId))
        {
            return VerifyRecordResultFactory.Build(false, OwnerOnlyMessage("更換這筆會議紀錄的影音檔"));
        }

        // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
        if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
        {
            return VerifyRecordResultFactory.Build(false, archivedError);
        }

        // 背景工作還在跑就不能換檔（0.4.115，比照重新轉錄）：舊檔那趟轉錄不會停，
        // 跑完會把舊錄音的逐字稿和「已完成」寫到已經換成新檔的會議上；生成中的會議紀錄也正在讀舊逐字稿。
        if (IsJobActive(meeting))
        {
            Logger.LogWarning("Meeting media upload rejected because a background job is active. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "這筆會議正在轉錄或產生會議紀錄，請等它完成，或從右下角的進度面板取消後再替換影音檔。");
        }

        // 上傳中還沒寫進資料庫，上面那道擋不到——同一筆會議在另一個視窗上傳到一半時，要靠通知器擋（0.4.117）。
        if (progressNotifier.Find(meetingId) is { Phase: TranscriptionPhase.Uploading })
        {
            Logger.LogWarning("Meeting media upload rejected because another upload is in progress. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "這筆會議正在其他視窗上傳影音檔，請等它完成。");
        }

        return null;
    }

    /// <summary>
    /// 重新把會議排入轉錄佇列（供「重新轉錄」按鈕使用）。
    /// 已排入（待處理）或正在轉錄中的紀錄一律拒絕，否則會重複入列、跑兩趟並重複計費。
    /// 佇列不持久化，重啟殘留的「待處理」由 Program.cs 的啟動修復改判為「失敗」後才能從這裡重跑。
    /// </summary>
    /// <param name="options">1.1.73：這次要用的引擎與分講者（呼叫端已驗證）；null＝沿用這場上次的選擇。</param>
    public async Task<VerifyRecordResult> RequeueTranscriptionAsync(
        int meetingId,
        Transcription.ResolvedTranscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
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

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Transcription requeue denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "Retranscribe");
                return VerifyRecordResultFactory.Build(false, "沒有權限對這筆會議紀錄執行轉錄。");
            }

            // 1.0.47：重新轉錄會覆蓋逐字稿，只限上傳者與管理者。
            if (!access.IsMeetingOwner(meeting.CreatedByUserId))
            {
                return VerifyRecordResultFactory.Build(false, OwnerOnlyMessage("重新轉錄這筆會議紀錄"));
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
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

            if (await CheckQuotaAsync(cancellationToken) is { } quotaMessage)
            {
                Logger.LogWarning("Transcription requeue blocked by AI quota. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, quotaMessage);
            }

            meeting.TranscriptionStatus = TranscriptionStatus.Pending;
            meeting.TranscriptionError = null;
            meeting.TranscriptionStartedAt = null;
            meeting.TranscriptionCompletedAt = null;
            if (options is not null)
            {
                meeting.TranscriptionEngine = options.Engine;
                meeting.TranscriptionDiarize = options.Diarize;
                meeting.TranscriptionMaxSpeakers = options.MaxSpeakers;
            }

            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            cancellationRegistry.ClearPendingCancel(BackgroundJobKind.Transcription, meetingId); // 1.1.68，理由見 SaveMediaAsync
            progressNotifier.Enqueued(meetingId, meeting.Title, meeting.ProjectId, meeting.CreatedByUserId);
            await transcriptionQueue.EnqueueAsync(BuildJobRequest(meetingId), cancellationToken);
            await AuditAsync(AuditActions.MeetingRetranscribe, meetingId, $"title={meeting.Title}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Attach denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "AttachProject");
                return VerifyRecordResultFactory.Build(false, "沒有權限變更這筆會議紀錄的歸屬。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, projectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
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
            // 看不到的專案當成不存在，不透露它存在與否。
            if (project is null || !access.CanViewProject(project.Id))
            {
                if (project is not null)
                {
                    await AuditDataScopeDeniedAsync(nameof(Project), project.Id, $"AttachProject; meetingId={meetingId}");
                }

                return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
            }

            // 只寫歸屬。草稿內容、提示詞快照、與會者快照、生成時間全部原封不動，
            // 也不重新入列——這支的存在理由就是「不必重跑、不必再付一次 API 費用」。
            meeting.ProjectId = projectId;
            meeting.UpdatedAt = DateTime.Now;

            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            await AuditAsync(AuditActions.MeetingAttachProject, meetingId, $"title={meeting.Title}; projectId={projectId}");
            Logger.LogInformation(
                "Meeting attached to project. MeetingId={MeetingId}, Title={Title}, ProjectId={ProjectId}",
                meetingId,
                meeting.Title,
                projectId);

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Detach denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "DetachProject");
                return VerifyRecordResultFactory.Build(false, "沒有權限移除這筆會議紀錄。");
            }

            // 1.0.47：從專案移除後只剩上傳者看得到，所以只限上傳者與管理者。
            if (!access.IsMeetingOwner(meeting.CreatedByUserId))
            {
                return VerifyRecordResultFactory.Build(false, OwnerOnlyMessage("把這筆會議紀錄從專案移除"));
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
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

            // 1.1.71：會議紀錄清掉了，舊版也一起刪（會議回到只有上傳者看得到，留著等於把專案的內容帶走）。
            context.MeetingDraftVersion.RemoveRange(context.MeetingDraftVersion.Where(x => x.MeetingId == meetingId));

            var detachedProjectId = meeting.ProjectId;
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

            await AuditAsync(AuditActions.MeetingDetachProject, meetingId, $"title={meeting.Title}; projectId={detachedProjectId}; draftCleared=true");
            Logger.LogInformation(
                "Meeting detached from project. MeetingId={MeetingId}, Title={Title}", meetingId, meeting.Title);

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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
            CleanTrackingHelper.Clean<Project>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要產生會議紀錄的逐字稿。");
            }

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Draft request denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "DraftRequest");
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

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, effectiveProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            // ⚠️ 這個 if 同樣不能省：x.Id == effectiveProjectId 在 null 時是 lifted comparison、
            // 恆為 false，查不到任何列就會讓「不指定專案」一律回「找不到指定的專案項目」。
            // 1.1.65：要追蹤——送出時會把名冊沒有的與會者補進 Project.Participants。
            Project? project = null;
            if (effectiveProjectId is not null)
            {
                project = await context.Project
                    .FirstOrDefaultAsync(x => x.Id == effectiveProjectId, cancellationToken);
                if (project is null || !access.CanViewProject(project.Id))
                {
                    if (project is not null)
                    {
                        await AuditDataScopeDeniedAsync(nameof(Project), project.Id, $"DraftRequest; meetingId={meetingId}");
                    }

                    return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
                }
            }

            var template = await context.PromptTemplate.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == promptTemplateId, cancellationToken);
            if (template is null || !template.IsEnabled)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的提示詞，或該提示詞已停用。");
            }

            if (await CheckQuotaAsync(cancellationToken) is { } quotaMessage)
            {
                Logger.LogWarning("Draft request blocked by AI quota. MeetingId={MeetingId}", meetingId);
                return VerifyRecordResultFactory.Build(false, quotaMessage);
            }

            // 1.1.71：要改寫提示詞與與會人員之前先留版，舊版才帶得到它自己的提示詞。
            await SnapshotDraftAsync(meeting, MeetingDraftVersionReasons.Regenerate, cancellationToken);

            meeting.ProjectId = effectiveProjectId;
            meeting.DraftPromptTemplateId = template.Id;
            meeting.DraftPromptTemplateName = template.Name;

            // ToStored 已負責 trim／去空／忽略大小寫去重／保順序／全空回 null。
            // Take 的唯一理由是界定提示詞大小，不是權限。
            meeting.DraftAttendees = TagStringHelper.ToStored((attendees ?? []).Take(MaxDraftAttendees));

            // 1.1.69：排入產生就不再等標示、也不再自動產生（避免之後重新轉錄又自動產生一次）。
            meeting.AwaitingSpeakerNames = false;
            meeting.AutoDraftPromptTemplateId = null;

            // 1.1.65：與會者可以在送出時直接輸入，名冊沒有的人補進專案名冊，和會議同一次存檔。
            var rosterAdded = project is null ? 0 : MergeIntoRoster(project, TagStringHelper.ToList(meeting.DraftAttendees));

            meeting.DraftStatus = DraftStatus.Pending;
            meeting.DraftError = null;
            meeting.DraftStartedAt = null;
            meeting.DraftCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);
            CleanTrackingHelper.Clean<Project>(context);

            // 先登錄進度再入列，理由同轉錄：背景工作要等輪到才知道，先登錄畫面才立刻看得到「排隊中」。
            cancellationRegistry.ClearPendingCancel(BackgroundJobKind.MeetingDraft, meetingId); // 1.1.68，理由見 SaveMediaAsync
            draftProgressNotifier.Enqueued(meetingId, meeting.Title, meeting.ProjectId, meeting.CreatedByUserId);
            await draftQueue.EnqueueAsync(BuildJobRequest(meetingId), cancellationToken);
            await AuditAsync(
                AuditActions.MeetingDraftRequest,
                meetingId,
                $"title={meeting.Title}; projectId={effectiveProjectId}; template={template.Name}; attendees={TagStringHelper.ToList(meeting.DraftAttendees).Count}; rosterAdded={rosterAdded}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to request meeting draft. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "排入會議紀錄生成失敗。", ex);
        }
    }

    /// <summary>
    /// 把名冊沒有的與會者接在專案名冊後面（忽略大小寫比對，保留原順序），回傳補了幾位。
    /// 沒有新名字時不動專案，避免無謂更新 UpdatedAt。
    /// </summary>
    private static int MergeIntoRoster(Project project, IReadOnlyCollection<string> attendees)
    {
        var roster = TagStringHelper.ToList(project.Participants);
        var known = new HashSet<string>(roster, StringComparer.OrdinalIgnoreCase);
        var added = attendees.Where(known.Add).ToList();
        if (added.Count == 0)
        {
            return 0;
        }

        project.Participants = TagStringHelper.ToStored(roster.Concat(added));
        project.UpdatedAt = DateTime.Now;
        return added.Count;
    }

    /// <summary>
    /// 取得可供「AI 轉會議紀錄」選用的逐字稿（轉錄已完成者）。
    /// 一併回傳歸屬資訊，讓畫面把已被其他專案取用的項目標示為不可選。
    /// </summary>
    public async Task<List<MeetingAdapterModel>> GetSelectableTranscriptsAsync(CancellationToken cancellationToken = default)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking()
            .Where(x => x.TranscriptionStatus == TranscriptionStatus.Completed);

        dataSource = (await projectAccess.GetAsync()).Filter(dataSource);

        var items = await dataSource
            .Include(x => x.Project)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return MapWithProjectTitle(items);
    }

    /// <summary>
    /// 讀出逐字稿全文，給「會議紀錄」視窗的逐字稿頁籤看、複製、下載（1.0.40）。
    ///
    /// <para>
    /// 0.4.82 移除過「人看逐字稿」的路徑；1.0.40 因使用者要拿逐字稿去外部 AI 問答而加回來，只開放唯讀。
    /// 守門與清單相同（看不到的會議當成不存在，回 null）；內容已過濾供應商指令外漏（見 <see cref="MeetingFileStore.ReadTranscriptAsync"/>）。
    /// </para>
    /// </summary>
    public async Task<string?> ReadTranscriptAsync(int meetingId, CancellationToken cancellationToken = default)
    {
        var source = await FindTranscriptSourceAsync(meetingId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        // 1.1.64：「講者 1：」換成使用者標示的真名（檔案本身不改）。
        var text = await fileStore.ReadTranscriptAsync(source.TranscriptRelativePath, cancellationToken);
        return text is null ? null : TranscriptSpeakerLabels.ApplyNames(text, TranscriptSpeakerLabels.ParseNames(source.SpeakerNames));
    }

    /// <summary>
    /// 「標示講者」視窗的資料（1.1.64）：逐字稿裡整場對齊的講者、目前的對照、幾句他說的話，
    /// 加上所屬專案的與會人員名冊。看不到的會議回 null。
    /// </summary>
    public async Task<MeetingRecord.Dtos.Meetings.MeetingSpeakersDto?> GetSpeakersAsync(int meetingId, CancellationToken cancellationToken = default)
    {
        var source = await FindTranscriptSourceAsync(meetingId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        var text = await fileStore.ReadTranscriptAsync(source.TranscriptRelativePath, cancellationToken);
        var names = TranscriptSpeakerLabels.ParseNames(source.SpeakerNames);
        var clips = TranscriptSpeakerLabels.ParseNames(source.SpeakerClips);

        return new MeetingRecord.Dtos.Meetings.MeetingSpeakersDto
        {
            Speakers = TranscriptSpeakerLabels.ListAlignedSpeakers(text)
                .Select(label => new MeetingRecord.Dtos.Meetings.MeetingSpeakerDto
                {
                    Label = label,
                    Name = names.GetValueOrDefault(label),
                    Samples = TranscriptSpeakerLabels.SampleLines(text, label).ToList(),
                    HasClip = clips.ContainsKey(label),
                })
                .ToList(),
            Participants = TagStringHelper.ToList(source.Participants),
            HasSegmentLabels = TranscriptSpeakerLabels.HasSegmentLabels(text),
            CanGenerate = source.CanGenerate,
        };
    }

    /// <summary>
    /// 1.1.71：某位講者試聽片段的實體路徑。看不到的會議、沒有這位講者或檔案不在都回 null（API 回 404）。
    /// </summary>
    public async Task<string?> GetSpeakerClipPathAsync(int meetingId, string label, CancellationToken cancellationToken = default)
    {
        var source = await FindTranscriptSourceAsync(meetingId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        return TranscriptSpeakerLabels.ParseNames(source.SpeakerClips).TryGetValue(label, out var relativePath)
            ? fileStore.FindSpeakerClipFullPath(relativePath)
            : null;
    }

    /// <summary>
    /// 1.1.69：標示講者並產生會議紀錄（上傳時選了「轉錄完自動產生」、轉完在等標示的會議）。
    /// 先存講者名字，再用上傳時選的提示詞走 <see cref="RequestDraftAsync"/>（權限、額度、封存等檢查都在那裡），
    /// 與會人員就是這次填的講者名字。名字全空＝「跳過，直接產生」。
    /// </summary>
    /// <param name="projectId">專案頁呼叫時帶專案 Id（同 RequestDraftAsync 的歸屬檢查）；會議頁帶 null。</param>
    public async Task<VerifyRecordResult> NameSpeakersAndGenerateAsync(
        int meetingId,
        IReadOnlyDictionary<string, string>? names,
        int? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var saved = await UpdateSpeakerNamesAsync(meetingId, names, cancellationToken);
        if (!saved.Success)
        {
            return saved;
        }

        var access = await projectAccess.GetAsync();
        var templateId = await access.Filter(context.Meeting.AsNoTracking())
            .Where(x => x.Id == meetingId)
            .Select(x => x.AutoDraftPromptTemplateId)
            .FirstOrDefaultAsync(cancellationToken);
        if (templateId is null)
        {
            return VerifyRecordResultFactory.Build(false, "講者名字已儲存，但這場會議沒有設定自動產生會議紀錄，請用「產生會議紀錄」手動產生。");
        }

        var attendees = (names ?? new Dictionary<string, string>())
            .Values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return await RequestDraftAsync(meetingId, projectId, templateId.Value, attendees, cancellationToken);
    }

    /// <summary>
    /// 儲存講者對照（1.1.64）。只接受逐字稿裡真的有的編號；名字留空代表不對照。
    /// 守門比照 <see cref="UpdateDraftAsync"/>：看得到這場會議、專案沒有封存。
    /// </summary>
    public async Task<VerifyRecordResult> UpdateSpeakerNamesAsync(
        int meetingId,
        IReadOnlyDictionary<string, string>? names,
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

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Speaker names update denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "SpeakerNamesUpdate");
                return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆會議紀錄。");
            }

            if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            var text = await fileStore.ReadTranscriptAsync(meeting.TranscriptRelativePath, cancellationToken);
            var labels = TranscriptSpeakerLabels.ListAlignedSpeakers(text).ToHashSet(StringComparer.Ordinal);
            var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (label, rawName) in names ?? new Dictionary<string, string>())
            {
                var name = rawName?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }

                if (!labels.Contains(label))
                {
                    return VerifyRecordResultFactory.Build(false, labels.Count == 0
                        ? "這份逐字稿沒有可以標示的講者，請先重新轉錄。"
                        : $"逐字稿裡沒有「講者 {label}」。");
                }

                if (!TranscriptSpeakerLabels.IsValidName(name))
                {
                    return VerifyRecordResultFactory.Build(
                        false, $"「{name}」不能當講者名字：最多 {TranscriptSpeakerLabels.MaxNameLength} 字，不能有換行或全形冒號。");
                }

                cleaned[label] = name;
            }

            meeting.SpeakerNames = TranscriptSpeakerLabels.SerializeNames(cleaned);
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            await AuditAsync(AuditActions.MeetingSpeakerNamesUpdate, meetingId, $"title={meeting.Title}; speakers={cleaned.Count}");
            Logger.LogInformation("Meeting speaker names updated. MeetingId={MeetingId}, Count={Count}", meetingId, cleaned.Count);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to update meeting speaker names. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "儲存講者名字失敗。", ex);
        }
    }

    private sealed record TranscriptSource(string? TranscriptRelativePath, string? SpeakerNames, string? Participants, bool CanGenerate = false, string? SpeakerClips = null);

    private async Task<TranscriptSource?> FindTranscriptSourceAsync(int meetingId, CancellationToken cancellationToken)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking()
            .Where(x => x.Id == meetingId);

        dataSource = (await projectAccess.GetAsync()).Filter(dataSource);

        return await dataSource
            .Select(x => new TranscriptSource(
                x.TranscriptRelativePath,
                x.SpeakerNames,
                x.Project == null ? null : x.Project.Participants,
                x.AwaitingSpeakerNames && x.AutoDraftPromptTemplateId != null,
                x.SpeakerClips))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>取得某專案底下的會議紀錄（歷史清單）。</summary>
    public async Task<List<MeetingAdapterModel>> GetByProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking()
            .Where(x => x.ProjectId == projectId);

        dataSource = (await projectAccess.GetAsync()).Filter(dataSource);

        var items = await dataSource
            .Include(x => x.Project)
            .OrderByDescending(x => x.DraftCompletedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return MapWithProjectTitle(items);
    }

    /// <summary>1.1.71：會議紀錄的舊版清單（新到舊）。看不到的會議回 null。</summary>
    public async Task<List<MeetingRecord.Dtos.Meetings.MeetingDraftVersionDto>?> GetDraftVersionsAsync(
        int meetingId,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewMeetingAsync(meetingId, cancellationToken))
        {
            return null;
        }

        var versions = await context.MeetingDraftVersion.AsNoTracking()
            .Where(x => x.MeetingId == meetingId)
            .OrderByDescending(x => x.SavedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new MeetingRecord.Dtos.Meetings.MeetingDraftVersionDto
            {
                Id = x.Id,
                SavedAt = x.SavedAt,
                ReasonText = x.Reason,
                PromptTemplateName = x.PromptTemplateName,
                SavedByName = x.SavedByName,
                ContentAt = x.ContentAt,
                Length = x.Content.Length,
            })
            .ToListAsync(cancellationToken);

        versions.ForEach(x => x.ReasonText = MeetingDraftVersionReasons.ToText(x.ReasonText));
        return versions;
    }

    /// <summary>1.1.71：某一份舊版的內容。看不到的會議或不屬於這場會議的版本回 null。</summary>
    public async Task<MeetingRecord.Dtos.Meetings.MeetingDraftVersionDetailDto?> GetDraftVersionAsync(
        int meetingId,
        int versionId,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewMeetingAsync(meetingId, cancellationToken))
        {
            return null;
        }

        var version = await context.MeetingDraftVersion.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == versionId && x.MeetingId == meetingId, cancellationToken);
        return version is null
            ? null
            : new MeetingRecord.Dtos.Meetings.MeetingDraftVersionDetailDto
            {
                Id = version.Id,
                SavedAt = version.SavedAt,
                ReasonText = MeetingDraftVersionReasons.ToText(version.Reason),
                PromptTemplateName = version.PromptTemplateName,
                SavedByName = version.SavedByName,
                ContentAt = version.ContentAt,
                Length = version.Content.Length,
                Content = version.Content,
                Attendees = TagStringHelper.ToList(version.Attendees),
            };
    }

    /// <summary>
    /// 1.1.71：還原成某一份舊版。目前的內容先留一版（原因「還原前」），再換成那一版的內容、提示詞與與會人員。
    /// 檢查比照 <see cref="UpdateDraftAsync"/>：資料範圍、封存唯讀、產生中不能還原。
    /// </summary>
    public async Task<VerifyRecordResult> RestoreDraftVersionAsync(
        int meetingId,
        int versionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
            if (meeting is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要還原的會議紀錄。");
            }

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Draft restore denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "DraftRestore");
                return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆會議紀錄。");
            }

            if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            if (meeting.DraftStatus is DraftStatus.Processing or DraftStatus.Pending)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄正在產生中，請等結束後再還原。");
            }

            var version = await context.MeetingDraftVersion.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == versionId && x.MeetingId == meetingId, cancellationToken);
            if (version is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到這個版本，可能已經超過保留份數被刪除。");
            }

            await SnapshotDraftAsync(meeting, MeetingDraftVersionReasons.Restore, cancellationToken);

            meeting.DraftContent = version.Content;
            meeting.DraftPromptTemplateName = version.PromptTemplateName;
            meeting.DraftAttendees = version.Attendees;
            meeting.DraftStatus = DraftStatus.Completed;
            meeting.DraftError = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);
            CleanTrackingHelper.Clean<MeetingDraftVersion>(context);

            await AuditAsync(AuditActions.MeetingDraftRestore, meetingId, $"title={meeting.Title}; versionId={versionId}; savedAt={version.SavedAt:yyyy/MM/dd HH:mm}");
            Logger.LogInformation("Meeting draft restored. MeetingId={MeetingId}, VersionId={VersionId}", meetingId, versionId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to restore meeting draft. MeetingId={MeetingId}, VersionId={VersionId}", meetingId, versionId);
            return VerifyRecordResultFactory.Build(false, "還原失敗，請稍後再試。", ex);
        }
    }

    /// <summary>看得到這場會議嗎（套資料範圍，看不到的當成不存在）。</summary>
    private async Task<bool> CanViewMeetingAsync(int meetingId, CancellationToken cancellationToken)
    {
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking().Where(x => x.Id == meetingId);
        dataSource = (await projectAccess.GetAsync()).Filter(dataSource);
        return await dataSource.AnyAsync(cancellationToken);
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

            var access = await projectAccess.GetAsync();
            if (!access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
            {
                Logger.LogWarning("Draft update denied by project access. MeetingId={MeetingId}", meetingId);
                await AuditDataScopeDeniedAsync(nameof(Meeting), meetingId, "DraftUpdate");
                return VerifyRecordResultFactory.Build(false, "沒有權限修改這筆會議紀錄。");
            }

            // 1.0.46：已完成（封存）的專案唯讀，管理者不受限。
            if (await ProjectArchiveRules.CheckWritableAsync(context, meeting.ProjectId, access) is { } archivedError)
            {
                return VerifyRecordResultFactory.Build(false, archivedError);
            }

            if (meeting.DraftStatus == DraftStatus.Processing)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄正在重新產生中，請稍候再編修。");
            }

            // 1.1.71：內容有變才留版。
            if (!string.Equals(meeting.DraftContent, content, StringComparison.Ordinal))
            {
                await SnapshotDraftAsync(meeting, MeetingDraftVersionReasons.Edit, cancellationToken);
            }

            meeting.DraftContent = content;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            // 只記字數，不記內容（會議紀錄全文）。
            await AuditAsync(AuditActions.MeetingDraftUpdate, meetingId, $"title={meeting.Title}; chars={content?.Length ?? 0}");
            Logger.LogInformation("Meeting draft updated. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整個 scope 共用的：失敗留下的追蹤實體會在同一個 scope 下一次存檔時被寫進去（0.4.115）。
            context.ChangeTracker.Clear();
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

    /// <summary>
    /// 同步轉發的進度回報。不用 <see cref="Progress{T}"/>：它會把回呼丟回建立當下的同步內容，
    /// 有同步內容時等於排隊等那條執行緒，進度會落後甚至在上傳結束後才補送。
    /// </summary>
    private sealed class ForwardingProgress(Action<int> onReport) : IProgress<int>
    {
        public void Report(int value) => onReport(value);
    }
}
