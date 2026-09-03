using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 把伺服器端產生的檔案內容直接推給瀏覽器下載。
///
/// 走 JS interop 而非 HTTP 端點是刻意的：所有 API controller 都是 JWT Bearer 驗證，
/// 瀏覽器導航帶的是 Cookie，<c>&lt;a href&gt;</c> 一定 401；且多開一個對外檔案輸出面
/// 就多一處要顧的授權（見 docs/features/檔案上傳機制.md）。這樣做檔案完全不落地。
///
/// 對應的前端實作在 <c>wwwroot/js/download.js</c>。
/// </summary>
public sealed class FileDownloadInterop
{
    private const string SaveAsFileFunction = "meetingRecordDownload.saveAsFile";

    private readonly IJSRuntime jsRuntime;

    public FileDownloadInterop(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    /// <summary>把位元組內容下載成檔案。</summary>
    /// <param name="contentType">Blob 的 MIME；決定瀏覽器怎麼看待這份檔案。</param>
    public async Task SaveBytesAsync(
        string fileName,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new MemoryStream(content);
        using var streamReference = new DotNetStreamReference(stream, leaveOpen: true);

        await jsRuntime.InvokeVoidAsync(SaveAsFileFunction, cancellationToken, fileName, streamReference, contentType);
    }
}
