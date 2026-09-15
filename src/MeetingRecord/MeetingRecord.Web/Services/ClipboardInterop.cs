using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 把文字複製到瀏覽器的剪貼簿。
///
/// <para>
/// ⚠️ <b>會失敗，而且不算罕見。</b>Blazor Server 的點擊要先送到伺服器、處理完再回頭呼叫 JS，
/// 此時瀏覽器認定的「使用者手勢」可能已經過期——Chromium 對作用中分頁通常放行，
/// Firefox 多半會擋；非安全來源（區網 HTTP）連 <c>navigator.clipboard</c> 都沒有。
/// </para>
///
/// <para>
/// 所以這裡回傳布林而不是 <c>Task</c>：呼叫端<b>必須</b>依結果決定顯示成功還是
/// 「請手動選取」，不要 fire-and-forget 假裝成功。
/// </para>
///
/// 對應的前端實作在 <c>wwwroot/js/clipboard.js</c>。
/// </summary>
public sealed class ClipboardInterop
{
    private const string CopyTextFunction = "meetingRecordClipboard.copyText";

    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<ClipboardInterop> logger;

    public ClipboardInterop(IJSRuntime jsRuntime, ILogger<ClipboardInterop> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    /// <summary>複製文字。回傳是否真的複製成功。</summary>
    public async Task<bool> CopyTextAsync(string? text, CancellationToken cancellationToken = default)
    {
        try
        {
            return await jsRuntime.InvokeAsync<bool>(CopyTextFunction, cancellationToken, text ?? string.Empty);
        }
        catch (JSException ex)
        {
            // 前端已經自己接過兩層例外，走到這裡多半是函式根本沒載入。
            logger.LogWarning(ex, "Clipboard interop failed.");
            return false;
        }
    }
}
