using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
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
    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly MeetingFileStore fileStore;
    private readonly ITranscriptionQueue transcriptionQueue;
    private readonly IMeetingDraftQueue draftQueue;

    public IMapper Mapper { get; }
    public ILogger<MeetingService> Logger { get; }

    public MeetingService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MeetingService> logger,
        IRecordAccessScopeProvider accessScope,
        MeetingFileStore fileStore,
        ITranscriptionQueue transcriptionQueue,
        IMeetingDraftQueue draftQueue)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
        this.fileStore = fileStore;
        this.transcriptionQueue = transcriptionQueue;
        this.draftQueue = draftQueue;
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
        IQueryable<Meeting> dataSource = context.Meeting.AsNoTracking();

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
        result.Result = Mapper.Map<List<MeetingAdapterModel>>(records);
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
            // ProjectId 刻意不在此列——歸屬本來就要能從畫面改。
            itemData.DraftContent = item.DraftContent;
            itemData.DraftStatus = item.DraftStatus;
            itemData.DraftError = item.DraftError;
            itemData.DraftPromptTemplateId = item.DraftPromptTemplateId;
            itemData.DraftPromptTemplateName = item.DraftPromptTemplateName;
            itemData.DraftStartedAt = item.DraftStartedAt;
            itemData.DraftCompletedAt = item.DraftCompletedAt;

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

            await transcriptionQueue.EnqueueAsync(meetingId, cancellationToken);

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
    /// 佇列不持久化，應用程式重啟後停留在「待處理」的紀錄也靠這個方法重跑。
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

            if (meeting.TranscriptionStatus == TranscriptionStatus.Processing)
            {
                return VerifyRecordResultFactory.Build(false, "這筆會議紀錄正在轉錄中，請稍候。");
            }

            meeting.TranscriptionStatus = TranscriptionStatus.Pending;
            meeting.TranscriptionError = null;
            meeting.TranscriptionStartedAt = null;
            meeting.TranscriptionCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            await transcriptionQueue.EnqueueAsync(meetingId, cancellationToken);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to requeue transcription. MeetingId={MeetingId}", meetingId);
            return VerifyRecordResultFactory.Build(false, "重新轉錄失敗。", ex);
        }
    }

    /// <summary>
    /// 讀取逐字稿全文供畫面預覽。越權或檔案不存在時回傳 null。
    /// </summary>
    public async Task<string?> ReadTranscriptAsync(int meetingId, CancellationToken cancellationToken = default)
    {
        var meeting = await context.Meeting
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);

        if (meeting is null)
        {
            return null;
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(meeting.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Transcript preview denied by team scope. MeetingId={MeetingId}", meetingId);
            return null;
        }

        return await fileStore.ReadTranscriptAsync(meeting.TranscriptRelativePath, cancellationToken);
    }

    #endregion

    #region AI 會議紀錄草稿

    /// <summary>
    /// 把會議排入草稿生成佇列，同時把它歸屬到指定專案。
    ///
    /// 一份逐字稿只能屬於一個專案：已被「其他」專案取用的會拒絕，
    /// 但屬於同一個專案的可以重跑（換提示詞重新生成，會覆蓋既有草稿）。
    /// </summary>
    public async Task<VerifyRecordResult> RequestDraftAsync(
        int meetingId,
        int projectId,
        int promptTemplateId,
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

            if (meeting.DraftStatus == DraftStatus.Processing)
            {
                return VerifyRecordResultFactory.Build(false, "這筆逐字稿正在產生會議紀錄中，請稍候。");
            }

            if (meeting.ProjectId is not null && meeting.ProjectId != projectId)
            {
                return VerifyRecordResultFactory.Build(false, "這份逐字稿已歸屬於其他專案，無法重複取用。");
            }

            var project = await context.Project.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的專案項目。");
            }

            if (!TagStringHelper.IsTeamAccessible(project.Teams, scope.Teams, scope.IsAdmin))
            {
                Logger.LogWarning("Draft request denied by project team scope. ProjectId={ProjectId}", projectId);
                return VerifyRecordResultFactory.Build(false, "沒有權限存取指定的專案項目。");
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

            meeting.ProjectId = projectId;
            meeting.DraftPromptTemplateId = template.Id;
            meeting.DraftPromptTemplateName = template.Name;
            meeting.DraftStatus = DraftStatus.Pending;
            meeting.DraftError = null;
            meeting.DraftStartedAt = null;
            meeting.DraftCompletedAt = null;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);
            CleanTrackingHelper.Clean<Meeting>(context);

            await draftQueue.EnqueueAsync(meetingId, cancellationToken);
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
