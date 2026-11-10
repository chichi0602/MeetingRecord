using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Jobs;

namespace MeetingRecord.Web.Jobs;

/// <summary>
/// 背景工作進度的伺服器端邏輯（1.1.15，原本寫在 <c>BackgroundJobProgressPanel</c> 裡）：
/// Server 頁的面板資料來源與 <c>JobController</c> 共用，可見性、取消、關閉的規則只有一份。
///
/// <para>
/// 通知器是全行程共用的，一律以呼叫者的資料範圍過濾（<see cref="ProjectAccess.CanViewMeeting"/>）——
/// 否則會把別人專案的會議標題洩漏給看不到那筆資料的使用者。取消與關閉也只能動自己看得到的工作，看不到的當成不存在。
/// </para>
/// <para>
/// ⚠️ 成員資格要用自己開的 scope 查：Server 頁的面板由背景事件觸發，拿頁面那個 DbContext 查會和頁面正在跑的查詢撞在一起。
/// </para>
/// </summary>
public sealed class BackgroundJobBoard(
    ITranscriptionProgressNotifier transcriptionNotifier,
    IMeetingDraftProgressNotifier draftNotifier,
    IRecordAccessScopeProvider accessScope,
    IServiceScopeFactory scopeFactory,
    IJobCancellationRegistry cancellationRegistry,
    ILogger<BackgroundJobBoard> logger)
{
    /// <summary>呼叫者看得到的工作；進行中的排前面，再依標題。</summary>
    public async Task<List<BackgroundJobDto>> GetVisibleAsync()
    {
        var scope = await accessScope.GetAsync();
        ProjectAccess access;
        using (var serviceScope = scopeFactory.CreateScope())
        {
            var context = serviceScope.ServiceProvider.GetRequiredService<BackendDBContext>();
            access = await ProjectAccessService.LoadAsync(context, scope);
        }

        var transcriptionJobs = transcriptionNotifier
            .GetSnapshot()
            .Where(x => access.CanViewMeeting(x.ProjectId, x.CreatedByUserId))
            .Select(FromTranscription);

        var draftJobs = draftNotifier
            .GetSnapshot()
            .Where(x => access.CanViewMeeting(x.ProjectId, x.CreatedByUserId))
            .Select(FromDraft);

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
        var job = (await GetVisibleAsync()).FirstOrDefault(x => x.Kind == kind && x.MeetingId == meetingId);
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

    /// <summary>關閉一則通知（工作照跑）。看不到的回 false。關閉是全域的：通知器只有一份，比照原本的面板。</summary>
    public async Task<bool> DismissAsync(string kind, int meetingId)
    {
        if (!(await GetVisibleAsync()).Any(x => x.Kind == kind && x.MeetingId == meetingId))
        {
            return false;
        }

        Dismiss(kind, meetingId);
        return true;
    }

    /// <summary>關閉呼叫者看得到的全部通知，回關閉的筆數。</summary>
    public async Task<int> DismissAllAsync()
    {
        var jobs = await GetVisibleAsync();
        foreach (var job in jobs)
        {
            Dismiss(job.Kind, job.MeetingId);
        }

        return jobs.Count;
    }

    private void Dismiss(string kind, int meetingId)
    {
        if (kind == BackgroundJobKinds.Transcription)
        {
            transcriptionNotifier.Dismiss(meetingId);
        }
        else
        {
            draftNotifier.Dismiss(meetingId);
        }
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
            TranscriptionPhase.Completed => "已完成",
            TranscriptionPhase.Failed => "失敗",
            _ => string.Empty,
        },
        IsRunning = item.IsRunning,
        IsCompleted = item.Phase == TranscriptionPhase.Completed,
        IsFailed = item.Phase == TranscriptionPhase.Failed,
        ErrorMessage = item.ErrorMessage,
        IsUploading = item.Phase == TranscriptionPhase.Uploading,
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
    };
}
