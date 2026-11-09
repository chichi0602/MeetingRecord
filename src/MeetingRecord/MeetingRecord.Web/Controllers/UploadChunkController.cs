using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 分塊上傳的「傳位元組」端點（1.0.59）。瀏覽器的 <c>wwwroot/js/chunk-upload.js</c> 依序送每一塊，
/// 伺服器端接進 <see cref="ChunkedUploadStore"/> 的工作階段，由網頁元件交給既有服務層讀取。
///
/// <para>
/// 這是「API 一律 JWT」的明文例外：呼叫端是已登入的網頁本身，走登入 Cookie。
/// 授權不靠 <c>[HasPermission]</c>，而是「工作階段的擁有者＝目前使用者」——工作階段是網頁在
/// 使用者通過所有權限與資料範圍檢查、按下存檔之後才建立的；找不到或不是自己的一律 404。
/// </para>
///
/// <para>
/// 因為走 Cookie，要防跨站請求偽造：必須帶自訂標頭 <see cref="CsrfHeaderName"/>。
/// 跨站的網頁要帶自訂標頭就得先過 CORS 預檢，而本站的 CORS 不放行其他網域帶 Cookie。
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/uploads")]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
// .NET 10 的 Cookie 驗證對 API 端點不導向登入頁、直接回空白 401；全站的 UseStatusCodePagesWithReExecute
// 會把空白 401 拿去重跑 /not-found，PUT 打到頁面變成 405，瀏覽器就誤判成網路問題一直重試。
[SkipStatusCodePages]
public class UploadChunkController : ControllerBase
{
    public const string CsrfHeaderName = "X-MeetingRecord-Upload";

    private readonly ChunkedUploadStore store;
    private readonly ILogger<UploadChunkController> logger;

    public UploadChunkController(ChunkedUploadStore store, ILogger<UploadChunkController> logger)
    {
        this.store = store;
        this.logger = logger;
    }

    /// <summary>
    /// 送第 <paramref name="index"/> 塊（從 0 起，body 是原始位元組）。
    /// 全站 "api" 限流是一個共用桶、每分鐘 120 次，1 GB 就要 128 塊，所以這支免除限流；
    /// 濫用的空間有限：只有自己剛建立、還沒結束的工作階段收得進資料，而且必須依序、長度剛好。
    /// </summary>
    [HttpPut("{uploadId:guid}/chunks/{index:int}")]
    [DisableRateLimiting]
    [RequestSizeLimit(ChunkedUploadStore.ChunkSize + 64 * 1024)]
    public async Task<ActionResult<ApiResult>> PutChunk(Guid uploadId, int index, CancellationToken cancellationToken)
    {
        if (!string.Equals(Request.Headers[CsrfHeaderName], "1", StringComparison.Ordinal))
        {
            logger.LogWarning("Upload chunk rejected because the CSRF header is missing. UploadId={UploadId}", uploadId);
            return BadRequest(ApiResult.ValidationError($"缺少 {CsrfHeaderName} 標頭。"));
        }

        if (!int.TryParse(User.FindFirst(ClaimTypes.Sid)?.Value, out var userId))
        {
            return Unauthorized(ApiResult.FailureResult("登入狀態無效，請重新登入。", StatusCodes.Status401Unauthorized));
        }

        if (!store.TryGet(uploadId, userId, out var session))
        {
            return NotFound(ApiResult.NotFoundResult("找不到這趟上傳，可能已經結束或逾時。"));
        }

        byte[] data;
        try
        {
            data = await ReadBodyAsync(Request.Body, ChunkedUploadStore.ChunkSize, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ApiResult.ValidationError(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // 瀏覽器在傳這一塊時斷線、關掉頁面或主動中止（取消上傳）：對方已經不在了，不是伺服器錯誤，
            // 不要讓它變成系統例外紀錄。這一塊沒寫進去，瀏覽器重送或工作階段逾時都會處理。
            logger.LogInformation("Upload chunk aborted by the client. UploadId={UploadId}, Index={Index}, Reason={Reason}", uploadId, index, ex.GetType().Name);
            return new EmptyResult();
        }

        ChunkWriteResult result;
        try
        {
            result = await session.WriteChunkAsync(index, data, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 排隊等寫入時瀏覽器已經走了；這一塊沒寫進去。
            logger.LogInformation("Upload chunk abandoned while waiting to be written. UploadId={UploadId}, Index={Index}", uploadId, index);
            return new EmptyResult();
        }

        logger.LogDebug(
            "Upload chunk handled. UploadId={UploadId}, Index={Index}, Status={Status}, Received={Received}/{Total}",
            uploadId,
            index,
            result.Status,
            result.ReceivedChunks,
            session.ChunkCount);

        return result.Status switch
        {
            ChunkWriteStatus.Accepted or ChunkWriteStatus.AlreadyReceived
                => Ok(ApiResult.SuccessResult($"已收到 {result.ReceivedChunks}/{session.ChunkCount} 塊")),
            ChunkWriteStatus.Completed
                => Ok(ApiResult.SuccessResult("檔案已全部收到")),
            ChunkWriteStatus.Rejected
                => BadRequest(ApiResult.ValidationError(result.Message ?? "分塊不正確。")),
            _ => Conflict(ApiResult.FailureResult(result.Message ?? "上傳已經停止。", StatusCodes.Status409Conflict)),
        };
    }

    /// <summary>整塊讀進記憶體再寫入：半塊寫進去之後連線斷掉的話，重送那一塊就會重複寫。</summary>
    internal static async Task<byte[]> ReadBodyAsync(Stream body, int maxLength, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > maxLength)
            {
                throw new InvalidDataException($"單一分塊不可超過 {maxLength} 位元組。");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
