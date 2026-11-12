using Microsoft.AspNetCore.SignalR;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Web.Hubs;

/// <summary>
/// 把兩個進度通知器的變動轉送到 <see cref="AppHub"/>（1.1.15）：送不帶資料的 <see cref="AppHub.JobsChanged"/>，
/// WASM 頁的進度面板收到後重查 API。
///
/// <para>
/// 進度回報很密（上傳每個百分比、轉錄每一段、生成每一批字），所以節流：第一個變動後等 <see cref="Interval"/> 才送一次，
/// 這段時間內的其他變動併進同一次；送出前就清掉旗標，送的期間再有變動會再排下一次，最後一個狀態不會漏掉。
/// 送不出去只記警告：回報進度的背景工作不能因為推播失敗而失敗。
/// </para>
/// </summary>
public sealed class JobProgressHubBridge(
    ITranscriptionProgressNotifier transcriptionNotifier,
    IMeetingDraftProgressNotifier draftNotifier,
    IHubContext<AppHub> hubContext,
    ILogger<JobProgressHubBridge> logger) : IHostedService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private int pending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        transcriptionNotifier.Changed += OnChanged;
        draftNotifier.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        transcriptionNotifier.Changed -= OnChanged;
        draftNotifier.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged()
    {
        if (Interlocked.Exchange(ref pending, 1) == 1)
        {
            return; // 已經排了一次，這次併進去。
        }

        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        try
        {
            await Task.Delay(Interval);
            Interlocked.Exchange(ref pending, 0);
            await hubContext.Clients.All.SendAsync(AppHub.JobsChanged);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref pending, 0);
            logger.LogWarning(ex, "Failed to push job progress change to hub.");
        }
    }
}
