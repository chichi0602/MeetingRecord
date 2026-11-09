using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Uploads;

/// <summary>
/// 網頁元件用的分塊上傳入口（1.0.59，scoped＝一條 Blazor 連線一個）。
///
/// <para>
/// 用法：把原本的 <c>file.OpenReadStream(...)</c> 換成
/// <c>await using var upload = await uploader.OpenAsync(dropZone.InputElement, fileIndex, file);</c>，
/// 再把 <see cref="ChunkedUploadSession.Stream"/> 交給服務層。瀏覽器端（<c>wwwroot/js/chunk-upload.js</c>）
/// 直接從 <c>&lt;input type="file"&gt;</c> 取檔、以 HTTP 一塊一塊送，位元組不經過 Blazor 連線。
/// </para>
///
/// <para>
/// 結束一律靠釋放工作階段（<c>await using</c>）：服務層存完、拒絕或被取消，釋放之後瀏覽器送下一塊就會被告知
/// 「已停止」而收手；這裡另外叫 JS 立刻停，省掉那一塊的來回。
/// </para>
/// </summary>
public sealed class BrowserFileUploader
{
    private const string StartFunction = "meetingRecordChunkUpload.start";
    private const string AbortFunction = "meetingRecordChunkUpload.abort";

    private readonly IJSRuntime jsRuntime;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly ChunkedUploadStore store;
    private readonly ILogger<BrowserFileUploader> logger;

    public BrowserFileUploader(
        IJSRuntime jsRuntime,
        AuthenticationStateProvider authenticationStateProvider,
        ChunkedUploadStore store,
        ILogger<BrowserFileUploader> logger)
    {
        this.jsRuntime = jsRuntime;
        this.authenticationStateProvider = authenticationStateProvider;
        this.store = store;
        this.logger = logger;
    }

    /// <summary>
    /// 開始上傳 <paramref name="input"/> 裡第 <paramref name="fileIndex"/> 個檔案，回傳讀取用的工作階段。
    /// <paramref name="fileName"/>／<paramref name="fileSize"/> 用來核對瀏覽器端拿到的是同一個檔案。
    /// ⚠️ 呼叫端在讀完之前不可以重建那個 input（<c>FileDropZone.Reset</c>），否則瀏覽器端取不到檔案。
    /// </summary>
    public async Task<BrowserFileUpload> OpenAsync(ElementReference input, int fileIndex, string fileName, long fileSize)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        if (!int.TryParse(state.User.FindFirst(ClaimTypes.Sid)?.Value, out var userId))
        {
            throw new InvalidOperationException("登入狀態無效，請重新登入後再上傳。");
        }

        var session = store.Create(userId, fileSize);
        var callback = new BrowserUploadCallback(session, logger);
        var reference = DotNetObjectReference.Create(callback);
        var upload = new BrowserFileUpload(session, reference, jsRuntime, logger);

        try
        {
            // JS 端只登記並啟動送檔迴圈就回來，不在這裡等傳完（JS interop 預設 1 分鐘逾時）。
            await jsRuntime.InvokeVoidAsync(
                StartFunction,
                input,
                fileIndex,
                fileName,
                fileSize,
                session.Id.ToString(),
                ChunkedUploadStore.ChunkSize,
                reference);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to start browser chunk upload. UploadId={UploadId}", session.Id);
            session.Fail("瀏覽器無法開始上傳，請重新整理頁面後再試一次。");
        }

        return upload;
    }

    internal static string AbortFunctionName => AbortFunction;
}

/// <summary>一趟瀏覽器分塊上傳。釋放時結束工作階段並叫瀏覽器停止送。</summary>
public sealed class BrowserFileUpload : IAsyncDisposable
{
    private readonly ChunkedUploadSession session;
    private readonly DotNetObjectReference<BrowserUploadCallback> reference;
    private readonly IJSRuntime jsRuntime;
    private readonly ILogger logger;

    internal BrowserFileUpload(
        ChunkedUploadSession session,
        DotNetObjectReference<BrowserUploadCallback> reference,
        IJSRuntime jsRuntime,
        ILogger logger)
    {
        this.session = session;
        this.reference = reference;
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    /// <summary>交給服務層讀的串流。</summary>
    public Stream Stream => session.Stream;

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync();

        try
        {
            await jsRuntime.InvokeVoidAsync(BrowserFileUploader.AbortFunctionName, session.Id.ToString());
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            // 使用者早就離開頁面時連線已經斷了（JSDisconnectedException 繼承 JSException），瀏覽器那邊本來就停了。
            logger.LogDebug(ex, "Browser chunk upload abort skipped. UploadId={UploadId}", session.Id);
        }

        reference.Dispose();
    }
}

/// <summary>給瀏覽器回呼用：重試用完、登入失效等送不下去的情況。</summary>
public sealed class BrowserUploadCallback
{
    private readonly ChunkedUploadSession session;
    private readonly ILogger logger;

    internal BrowserUploadCallback(ChunkedUploadSession session, ILogger logger)
    {
        this.session = session;
        this.logger = logger;
    }

    [JSInvokable]
    public void OnFailed(string message)
    {
        logger.LogWarning("Browser chunk upload failed. UploadId={UploadId}, Message={Message}", session.Id, message);
        session.Fail(string.IsNullOrWhiteSpace(message) ? "上傳失敗，請重新上傳。" : message);
    }
}
