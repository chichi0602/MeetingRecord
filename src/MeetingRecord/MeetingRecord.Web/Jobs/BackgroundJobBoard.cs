using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Jobs;

namespace MeetingRecord.Web.Jobs;

/// <summary>
/// 背景工作進度的伺服器端邏輯（1.1.15）：
/// 給 <c>JobController</c>（WASM 頁右下角的進度面板）用，可見性、取消、關閉的規則只有一份。
///
/// <para>
/// 通知器是全行程共用的，一律以呼叫者的資料範圍過濾（<see cref="ProjectAccess.CanViewMeeting"/>）——
/// 否則會把別人專案的會議標題洩漏給看不到那筆資料的使用者。取消與關閉也只能動自己看得到的工作，看不到的當成不存在。
/// </para>
/// </summary>
public sealed class BackgroundJobBoard(
    ITranscriptionProgressNotifier transcriptionNotifier,
    IMeetingDraftProgressNotifier draftNotifier,
    ProjectAccessService projectAccessService,
    IJobCancellationRegistry cancellationRegistry,
    ILogger<BackgroundJobBoard> logger)
{
    /// <summary>呼叫者看得到、而且自己沒關掉的工作；進行中的排前面，再依標題。</summary>
    public async Task<List<BackgroundJobDto>> GetVisibleAsync()
        => GetVisible(await projectAccessService.GetAsync(), includeDismissed: false);

    /// <param name="includeDismissed">
    /// 1.1.68：關閉通知是每人各自的，自己關掉的工作仍然存在、也仍然看得到資料。
    /// 取消要能動它（工作還在跑），所以取消用 true；面板清單與關閉用 false。
    /// </param>
    private List<BackgroundJobDto> GetVisible(ProjectAccess access, bool includeDismissed)
    {
        var transcriptionJobs = transcriptionNotifier
            .GetSnapshot()
            .Where(x => access.CanViewMeeting(x.ProjectId, x.CreatedByUserId))
            .Where(x => includeDismissed || !transcriptionNotifier.IsDismissedBy(x.MeetingId, access.UserId))
            .Select(x => MarkMine(FromTranscription(x), x.CreatedByUserId, access.UserId));

        var draftJobs = draftNotifier
            .GetSnapshot()
            .Where(x => access.CanViewMeeting(x.ProjectId, x.CreatedByUserId))
            .Where(x => includeDismissed || !draftNotifier.IsDismissedBy(x.MeetingId, access.UserId))
            .Select(x => MarkMine(FromDraft(x), x.CreatedByUserId, access.UserId));

        // 進行中的排前面，讓仍在跑的工作不會被一堆已完成的項目擠下去。
        return [.. transcriptionJobs
            .Concat(draftJobs)
            .OrderByDescending(x => x.IsRunning)
            .ThenBy(x => x.Title)];
    }

    /// <summary>
    /// 真正取消工作：排隊中的會在 worker 取出時被跳過，執行中的會直接中斷。
    /// 看不到、或已經不在進行中，回 false。權限（會議紀錄的修改）由呼叫端檢查。
    /// </summary>
    public async Task<bool> CancelAsync(string kind, int meetingId)
    {
        var job = GetVisible(await projectAccessService.GetAsync(), includeDismissed: true).FirstOrDefault(x => x.Kind == kind && x.MeetingId == meetingId);
        if (job is null || !job.CanBeCancelled)
        {
            return false;
        }

        // 上傳中（1.0.37）中斷的是寫檔，結果由發起上傳的會議頁通知使用者。
        // ⚠️ 上傳要用 Upload 種類取消，不能用 Transcription：後者找不到執行中的工作會留下「待取消」標記，
        // 上傳完入列的那趟轉錄就被默默跳過。
        var jobKind = job.IsUploading
            ? BackgroundJobKind.Upload
            : job.Kind == BackgroundJobKinds.Transcription
                ? BackgroundJobKind.Transcription
                : BackgroundJobKind.MeetingDraft;

        cancellationRegistry.RequestCancel(jobKind, meetingId);
        logger.LogInformation("Background job cancellation requested. Kind={Kind}, MeetingId={MeetingId}", jobKind, meetingId);
        return true;
    }

    /// <summary>
    /// 關閉一則通知（工作照跑）。看不到的回 false。
    /// 1.1.68 起**只關自己的**：以前直接刪掉通知器裡的項目，一個人關掉所有人都看不到，
    /// 連進行中工作之後的完成／失敗也不會再出現。同一筆重新排入時會再出現（新的一趟）。
    /// </summary>
    public async Task<bool> DismissAsync(string kind, int meetingId)
    {
        var access = await projectAccessService.GetAsync();
        if (!GetVisible(access, includeDismissed: false).Any(x => x.Kind == kind && x.MeetingId == meetingId))
        {
            return false;
        }

        Dismiss(kind, meetingId, access.UserId);
        return true;
    }

    /// <summary>關閉呼叫者看得到的全部通知（只關自己的），回關閉的筆數。</summary>
    public async Task<int> DismissAllAsync()
    {
        var access = await projectAccessService.GetAsync();
        var jobs = GetVisible(access, includeDismissed: false);
        var userId = access.UserId;
        foreach (var job in jobs)
        {
            Dismiss(job.Kind, job.MeetingId, userId);
        }

        return jobs.Count;
    }

    private void Dismiss(string kind, int meetingId, int userId)
    {
        if (kind == BackgroundJobKinds.Transcription)
        {
            transcriptionNotifier.DismissFor(meetingId, userId);
        }
        else
        {
            draftNotifier.DismissFor(meetingId, userId);
        }
    }

    /// <summary>1.1.69：標記是不是呼叫者自己上傳的會議（完成提示只對自己的跳）。</summary>
    private static BackgroundJobDto MarkMine(BackgroundJobDto job, int? createdByUserId, int userId)
    {
        job.IsMine = createdByUserId is { } owner && owner == userId;
        return job;
    }

    public static BackgroundJobDto FromTranscription(TranscriptionProgressItem item) => new()
    {
        Kind = BackgroundJobKinds.Transcription,
        MeetingId = item.MeetingId,
        Title = item.Title,
        Percent = item.Percent,
        PhaseText = item.Phase switch
        {
            TranscriptionPhase.Uploading => "上傳中",
            TranscriptionPhase.Queued => "排隊中",
            TranscriptionPhase.Converting => "轉檔中",
            TranscriptionPhase.Transcribing => $"轉錄中（第 {item.CompletedSegments}/{item.TotalSegments} 段）",
            TranscriptionPhase.Completed => string.IsNullOrEmpty(item.Note) ? "已完成" : $"已完成，{item.Note}",
            TranscriptionPhase.Failed => "失敗",
            _ => string.Empty,
        },
        IsRunning = item.IsRunning,
        IsCompleted = item.Phase == TranscriptionPhase.Completed,
        IsFailed = item.Phase == TranscriptionPhase.Failed,
        ErrorMessage = item.ErrorMessage,
        IsUploading = item.Phase == TranscriptionPhase.Uploading,
        ProjectId = item.ProjectId,
        NeedsSpeakerNames = item.Phase == TranscriptionPhase.Completed && item.Note == TranscriptionJobRunner.AwaitingSpeakerNamesNote,
    };

    public static BackgroundJobDto FromDraft(MeetingDraftProgressItem item) => new()
    {
        Kind = BackgroundJobKinds.Draft,
        MeetingId = item.MeetingId,
        Title = item.Title,
        Percent = item.Percent,
        PhaseText = item.Phase switch
        {
            MeetingDraftPhase.Queued => "排隊中",
            MeetingDraftPhase.Preparing => "讀取逐字稿與提示詞",
            MeetingDraftPhase.Summarizing => $"分段摘要中（第 {item.CompletedChunks}/{item.TotalChunks} 段）",
            MeetingDraftPhase.Generating => $"產生會議紀錄中（已產生 {item.GeneratedCharacters} 字）",
            MeetingDraftPhase.Completed => "已完成",
            MeetingDraftPhase.Failed => "失敗",
            _ => string.Empty,
        },
        IsRunning = item.IsRunning,
        IsCompleted = item.Phase == MeetingDraftPhase.Completed,
        IsFailed = item.Phase == MeetingDraftPhase.Failed,
        ErrorMessage = item.ErrorMessage,
        ProjectId = item.ProjectId,
    };
}
