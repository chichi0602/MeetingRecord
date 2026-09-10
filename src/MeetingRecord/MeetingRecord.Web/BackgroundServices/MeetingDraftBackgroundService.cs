using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Enums;
using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 逐一取出 <see cref="IMeetingDraftQueue"/> 的待生成會議並產生會議紀錄草稿。
///
/// <para>
/// 與 <see cref="TranscriptionBackgroundService"/> 對稱，但刻意是獨立的一條軌道：
/// 轉錄一筆可能跑數十分鐘，共用 worker 會讓草稿生成被長音檔堵住。
/// </para>
/// <para>
/// 單一 worker：生成同樣受外部 API 速率限制，併發只會互相拖慢。
/// 每筆工作各自建立 DI scope 取用 Scoped 的 <see cref="MeetingDraftJobRunner"/>
/// （內含 Scoped 的 <c>BackendDBContext</c>）；單筆失敗不得讓整個背景服務結束。
/// </para>
/// </summary>
public sealed class MeetingDraftBackgroundService : BackgroundService
{
    private readonly IMeetingDraftQueue queue;
    private readonly IJobCancellationRegistry cancellationRegistry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<MeetingDraftBackgroundService> logger;

    public MeetingDraftBackgroundService(
        IMeetingDraftQueue queue,
        IJobCancellationRegistry cancellationRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<MeetingDraftBackgroundService> logger)
    {
        this.queue = queue;
        this.cancellationRegistry = cancellationRegistry;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>
    /// 排隊中就被取消的工作：runner 根本不會跑，狀態得由這裡收尾，
    /// 否則會永遠卡在「待處理」等一個不會來的 worker。
    /// </summary>
    private async Task MarkQueuedJobCancelledAsync(IServiceProvider services, int meetingId)
    {
        try
        {
            var context = services.GetRequiredService<BackendDBContext>();
            var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId);
            if (meeting is null)
            {
                return;
            }

            meeting.DraftStatus = DraftStatus.Cancelled;
            meeting.DraftError = null;
            meeting.DraftCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();

            services.GetRequiredService<IMeetingDraftProgressNotifier>()
                .ReportFailed(meetingId, "已由使用者取消。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist queued draft cancellation. MeetingId={MeetingId}", meetingId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Meeting draft background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int meetingId;
            try
            {
                meetingId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();

                // 還在排隊時就被取消：直接跳過，不要浪費一次 API 呼叫。
                if (cancellationRegistry.TryConsumePendingCancel(BackgroundJobKind.MeetingDraft, meetingId))
                {
                    logger.LogInformation("Draft generation skipped because it was cancelled while queued. MeetingId={MeetingId}", meetingId);
                    await MarkQueuedJobCancelledAsync(scope.ServiceProvider, meetingId);
                    continue;
                }

                using var handle = cancellationRegistry.BeginJob(BackgroundJobKind.MeetingDraft, meetingId, stoppingToken);
                var runner = scope.ServiceProvider.GetRequiredService<MeetingDraftJobRunner>();

                await runner.RunAsync(meetingId, handle.Token, () => handle.IsCancelledByUser);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Draft generation cancelled by shutdown. MeetingId={MeetingId}", meetingId);
                break;
            }
            catch (Exception ex)
            {
                // MeetingDraftJobRunner 內部已處理並記錄失敗；這裡是最後一道保險，
                // 確保任何非預期例外都不會讓背景服務整個停掉。
                logger.LogError(ex, "Unhandled error while running draft generation job. MeetingId={MeetingId}", meetingId);
            }
        }

        logger.LogInformation("Meeting draft background service stopped.");
    }
}
