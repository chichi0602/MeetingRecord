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
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<TranscriptionBackgroundService> logger;

    public TranscriptionBackgroundService(
        ITranscriptionQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<TranscriptionBackgroundService> logger)
    {
        this.queue = queue;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
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
                var runner = scope.ServiceProvider.GetRequiredService<TranscriptionJobRunner>();
                await runner.RunAsync(meetingId, stoppingToken);
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
