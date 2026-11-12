using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Models.Others;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Uploads;

/// <summary>
/// WASM 頁的會議影音檔上傳（1.1.16，singleton）。
///
/// <para>
/// 頁面和伺服器之間沒有一直連著的連線，不能讓請求一路等到檔案存完：API 建好工作階段就回應，
/// 讀串流（<see cref="ChunkedUploadSession.Stream"/>）、呼叫 <c>MeetingService.SaveMediaAsync</c> 存檔在伺服器背景跑，
/// 瀏覽器以 HTTP 一塊一塊送，最後以 <c>GET api/v1/uploads/{id}</c> 查結果。驗證、進度（共用通知器）、
/// 從進度面板取消、取消時刪會議、失敗原因全部沿用服務層，這裡一行規則都不寫。
/// </para>
///
/// <para>
/// ⚠️ 背景沒有請求，也就沒有登入身分：自開 scope，把上傳者填進 <see cref="CurrentUserService"/>——
/// 資料範圍（<c>RecordAccessScopeProvider</c>）、稽核的操作者（<c>AuditActorProvider</c>）與 AI 用量的觸發者（<c>AiCallerProvider</c>）都優先讀它。
/// 起背景工作時不帶執行內容（<see cref="ExecutionContext.SuppressFlow"/>），免得拿到已經結束的請求的 HttpContext。
/// </para>
/// </summary>
public sealed class MeetingMediaUploadJobs(IServiceScopeFactory scopeFactory, ChunkedUploadStore store, ILogger<MeetingMediaUploadJobs> logger)
{
    /// <summary>結束後保留多久讓前端查結果。</summary>
    public static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, MeetingMediaUploadJob> jobs = new();

    /// <summary>建立工作階段並在背景開始讀檔存檔。呼叫前要先通過 <c>MeetingService.CheckMediaUploadAsync</c>。</summary>
    public MeetingMediaUploadJob Start(int userId, UploadSessionRequestDto request) => Start(userId, request, beforeRun: null);

    /// <summary><paramref name="beforeRun"/> 只給測試用：在背景工作開始前動手腳（例如先取消），讓競態可以穩定重現。</summary>
    internal MeetingMediaUploadJob Start(int userId, UploadSessionRequestDto request, Action<MeetingMediaUploadJob>? beforeRun)
    {
        PurgeExpired();

        var session = store.Create(userId, request.Size);
        var job = new MeetingMediaUploadJob(session.Id, userId, request.MeetingId, session.ChunkCount);
        jobs[job.UploadId] = job;
        beforeRun?.Invoke(job);

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => RunAsync(job, session, request));
        }

        return job;
    }

    /// <summary>找自己的工作；不存在或不是自己的一律 false（呼叫端回 404，不透露存不存在）。</summary>
    public bool TryGet(Guid uploadId, int userId, out MeetingMediaUploadJob job)
    {
        if (jobs.TryGetValue(uploadId, out var found) && found.OwnerUserId == userId)
        {
            job = found;
            return true;
        }

        job = null!;
        return false;
    }

    private async Task RunAsync(MeetingMediaUploadJob job, ChunkedUploadSession session, UploadSessionRequestDto request)
    {
        try
        {
            await using (session)
            using (var scope = scopeFactory.CreateScope())
            {
                var services = scope.ServiceProvider;
                if (await LoadUserAsync(services, job.OwnerUserId) is not { } user)
                {
                    job.Finish(UploadStates.Failed, "登入狀態無效，請重新登入後再上傳。");
                    return;
                }

                services.GetRequiredService<CurrentUserService>().CurrentUser = user;
                var result = await services.GetRequiredService<MeetingService>().SaveMediaAsync(
                    job.MeetingId,
                    new MeetingMediaUploadInput
                    {
                        FileName = request.FileName,
                        ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
                        FileSize = request.Size,
                        Content = session.Stream,
                    },
                    cancellationToken: job.Cancellation.Token,
                    deleteMeetingOnCancel: request.DeleteMeetingOnCancel);

                if (result.Success)
                {
                    // 取消只作用在寫檔：按下取消時檔案已經寫完，就會走到這裡（「來不及取消」）。
                    job.Finish(UploadStates.Succeeded, null, cancelTooLate: job.Cancellation.IsCancellationRequested);
                }
                else if (result.Message == MeetingService.UploadCancelledAndMeetingDeletedMessage)
                {
                    job.Finish(UploadStates.Cancelled, result.Message, meetingDeleted: true);
                }
                else if (result.Message == MeetingService.UploadCancelledMessage)
                {
                    // 頁面建好工作階段就能取消，可能早在服務層開始收檔之前；
                    // 那時服務層還沒開始上傳，不會替「新增時附檔」的會議刪資料列，這裡補刪，結果與上傳中取消一致。
                    if (request.DeleteMeetingOnCancel
                        && (await services.GetRequiredService<MeetingService>().DeleteAsync(job.MeetingId)).Success)
                    {
                        services.GetRequiredService<ITranscriptionProgressNotifier>().Dismiss(job.MeetingId);
                        job.Finish(UploadStates.Cancelled, MeetingService.UploadCancelledAndMeetingDeletedMessage, meetingDeleted: true);
                    }
                    else
                    {
                        job.Finish(UploadStates.Cancelled, result.Message);
                    }
                }
                else
                {
                    job.Finish(UploadStates.Failed, result.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background meeting media upload failed. UploadId={UploadId}, MeetingId={MeetingId}", job.UploadId, job.MeetingId);
            job.Finish(UploadStates.Failed, "影音檔上傳失敗。");
        }
    }

    /// <summary>比照 <c>RecordAccessScopeProvider</c> 由 claims 載入的那一段：啟用中的帳號、管理者旗標、有效團隊。</summary>
    private static async Task<CurrentUser?> LoadUserAsync(IServiceProvider services, int userId)
    {
        var context = services.GetRequiredService<BackendDBContext>();
        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId && x.Status);
        if (user is null)
        {
            return null;
        }

        var teams = await services.GetRequiredService<IEffectiveTeamResolver>().GetEffectiveTeamNamesAsync(userId);
        return new CurrentUser
        {
            Id = user.Id,
            Account = user.Account,
            Name = user.Name,
            Email = user.Email,
            IsAdmin = user.IsAdmin,
            IsAuthenticated = true,
            TeamList = [.. teams],
        };
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.Now - ResultRetention;
        foreach (var (id, job) in jobs)
        {
            if (job.EndedAt is { } endedAt && endedAt < cutoff)
            {
                jobs.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>一趟 WASM 頁的會議影音檔上傳。</summary>
public sealed class MeetingMediaUploadJob(Guid uploadId, int ownerUserId, int meetingId, int chunkCount)
{
    private volatile string state = UploadStates.Uploading;

    public Guid UploadId { get; } = uploadId;

    public int OwnerUserId { get; } = ownerUserId;

    public int MeetingId { get; } = meetingId;

    public int ChunkCount { get; } = chunkCount;

    /// <summary><c>DELETE api/v1/uploads/{id}</c> 觸發（使用者按「取消上傳」）。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public string State => state;

    public string? Message { get; private set; }

    public bool MeetingDeleted { get; private set; }

    public bool CancelTooLate { get; private set; }

    public DateTime? EndedAt { get; private set; }

    internal void Finish(string finalState, string? message, bool meetingDeleted = false, bool cancelTooLate = false)
    {
        Message = message;
        MeetingDeleted = meetingDeleted;
        CancelTooLate = cancelTooLate;
        EndedAt = DateTime.Now;
        state = finalState;
    }

    public UploadStatusDto ToDto() => new()
    {
        UploadId = UploadId,
        MeetingId = MeetingId,
        State = State,
        Message = Message,
        MeetingDeleted = MeetingDeleted,
        CancelTooLate = CancelTooLate,
    };
}
