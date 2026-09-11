using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Enums;
using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 逐一取出 <see cref="ITranscriptionQueue"/> 的待轉錄會議並執行轉錄。
///
/// <para>
/// 單一 worker：轉錄同時受外部 API 速率限制與 FFmpeg 的 CPU 佔用影響，
/// 併發只會互相拖慢，也不符合本專案的單一實例假設。
/// </para>
/// <para>
/// 每筆工作各自建立 DI scope 取用 Scoped 的 <see cref="TranscriptionJobRunner"/>
/// （內含 Scoped 的 <c>BackendDBContext</c>）；單筆失敗不得讓整個背景服務結束。
/// </para>
/// </summary>
public sealed class TranscriptionBackgroundService : BackgroundService
{
    private readonly ITranscriptionQueue queue;
    private readonly IJobCancellationRegistry cancellationRegistry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<TranscriptionBackgroundService> logger;

    public TranscriptionBackgroundService(
        ITranscriptionQueue queue,
        IJobCancellationRegistry cancellationRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<TranscriptionBackgroundService> logger)
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

            meeting.TranscriptionStatus = TranscriptionStatus.Cancelled;
            meeting.TranscriptionError = null;
            meeting.TranscriptionCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();

            services.GetRequiredService<ITranscriptionProgressNotifier>()
                .ReportFailed(meetingId, "已由使用者取消，可從清單重新轉錄。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist queued transcription cancellation. MeetingId={MeetingId}", meetingId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Transcription background service started.");

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
                if (cancellationRegistry.TryConsumePendingCancel(BackgroundJobKind.Transcription, meetingId))
                {
                    logger.LogInformation("Transcription skipped because it was cancelled while queued. MeetingId={MeetingId}", meetingId);
                    await MarkQueuedJobCancelledAsync(scope.ServiceProvider, meetingId);
                    continue;
                }

                using var handle = cancellationRegistry.BeginJob(BackgroundJobKind.Transcription, meetingId, stoppingToken);
                var runner = scope.ServiceProvider.GetRequiredService<TranscriptionJobRunner>();

                await runner.RunAsync(meetingId, handle.Token, () => handle.IsCancelledByUser);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Transcription cancelled by shutdown. MeetingId={MeetingId}", meetingId);
                break;
            }
            catch (Exception ex)
            {
                // TranscriptionJobRunner 內部已處理並記錄失敗；這裡是最後一道保險，
                // 確保任何非預期例外都不會讓背景服務整個停掉。
                logger.LogError(ex, "Unhandled error while running transcription job. MeetingId={MeetingId}", meetingId);
            }
        }

        logger.LogInformation("Transcription background service stopped.");
    }
}
