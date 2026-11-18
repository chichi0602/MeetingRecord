using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 1.1.68 成本閘門：在瀏覽器讀出選好的影音檔長度（秒），讓上傳確認視窗先顯示預估轉錄費用。
/// 讀不到回 null（格式不支援、逾時），呼叫端照舊確認、只是不顯示金額。
/// 對應的前端實作在 <c>wwwroot/js/media-duration.js</c>。
/// </summary>
public sealed class MediaDurationInterop(IJSRuntime jsRuntime, ILogger<MediaDurationInterop> logger)
{
    private const string GetDurationFunction = "meetingRecordMedia.getDuration";

    private const int TimeoutMilliseconds = 5000;

    public async Task<double?> GetDurationAsync(ElementReference fileInput)
    {
        try
        {
            return await jsRuntime.InvokeAsync<double?>(GetDurationFunction, fileInput, TimeoutMilliseconds);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Reading media duration in the browser failed.");
            return null;
        }
    }
}
