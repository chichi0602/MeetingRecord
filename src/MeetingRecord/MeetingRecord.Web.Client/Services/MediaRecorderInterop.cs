using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 1.1.71：在瀏覽器直接錄音（上傳視窗的「錄音」）。對應的前端實作在 <c>wwwroot/js/media-recorder.js</c>。
/// 錄好的聲音留在 JS 端，這裡只拿到一把鑰匙（<see cref="RecordedAudio.Key"/>），上傳時交給 <c>MeetingUploadManager.StartRecordingAsync</c>。
/// </summary>
public sealed class MediaRecorderInterop(IJSRuntime jsRuntime, ILogger<MediaRecorderInterop> logger)
{
    /// <summary>開始錄音（會跳麥克風權限）。成功回 null，否則回給使用者看的原因（含瀏覽器不支援）。</summary>
    public async Task<string?> StartAsync()
    {
        try
        {
            // 等使用者按允許麥克風可能很久，不套預設的 1 分鐘逾時。
            return await jsRuntime.InvokeAsync<string?>("meetingRecordRecorder.start", CancellationToken.None, []);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Starting recording failed.");
            return "無法開始錄音，請重新整理頁面後再試一次。";
        }
    }

    /// <summary>停止錄音，回傳錄好的聲音；沒在錄音或失敗回 null。</summary>
    public async Task<RecordedAudio?> StopAsync()
    {
        try
        {
            return await jsRuntime.InvokeAsync<RecordedAudio?>("meetingRecordRecorder.stop");
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Stopping recording failed.");
            return null;
        }
    }

    /// <summary>錄音中放棄（關視窗、按取消）。</summary>
    public async Task CancelAsync()
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("meetingRecordRecorder.cancel");
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Cancelling recording failed.");
        }
    }

    /// <summary>上傳開始不了時把錄音存到使用者電腦。</summary>
    public async Task SaveToDiskAsync(string key, string fileName)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("meetingRecordRecorder.saveToDisk", key, fileName);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Saving recording to disk failed.");
        }
    }

    /// <summary>錄好但不要了（重錄、移除、關視窗）。</summary>
    public async Task DiscardAsync(string key)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("meetingRecordRecorder.discard", key);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Discarding recording failed.");
        }
    }
}

/// <summary>1.1.71：錄好的聲音（本體留在 JS 端）。</summary>
public sealed class RecordedAudio
{
    public string Key { get; set; } = string.Empty;

    public long Size { get; set; }

    public string MimeType { get; set; } = string.Empty;

    /// <summary>含點的副檔名，例如 <c>.webm</c>。</summary>
    public string Extension { get; set; } = string.Empty;
}
