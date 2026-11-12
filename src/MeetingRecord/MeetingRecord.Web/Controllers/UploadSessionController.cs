using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 上傳工作階段（1.1.16）：WASM 頁上傳會議影音檔用。建立 → 瀏覽器以
/// <c>PUT api/v1/uploads/{id}/chunks/{i}</c>（<see cref="UploadChunkController"/>，只認網頁 Cookie）送分塊 → 查狀態。
/// 讀檔、存檔、排入轉錄在伺服器背景跑（<see cref="MeetingMediaUploadJobs"/>），規則全在 <c>MeetingService</c>。
///
/// <para>
/// 建立前先跑 <c>MeetingService.CheckMediaUploadAsync</c>（格式、大小、資料範圍、只限上傳者、封存、進行中工作），
/// 不合直接回 400 與原因。查詢與取消只認工作階段的擁有者，別人的一律 404。
/// </para>
/// </summary>
[Route("api/v1/uploads")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class UploadSessionController(MeetingService meetingService, MeetingMediaUploadJobs uploadJobs, ProjectAttachmentUploads attachmentUploads, ChunkedUploadStore store) : ControllerBase
{
    /// <summary>
    /// 建立上傳工作階段。新增會議時附檔（要「新增」權限）與修改時換檔（要「修改」權限）都走這支，所以任一即可；
    /// 「只限上傳者」等資料層級的規則由服務層把關。
    /// </summary>
    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<UploadSessionDto>>> Create([FromBody] UploadSessionRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<UploadSessionDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (request.Purpose != UploadPurposes.MeetingMedia)
        {
            return BadRequest(ApiResult<UploadSessionDto>.FailureResult("不支援的上傳用途。"));
        }

        if (request.Size < 0 || string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest(ApiResult<UploadSessionDto>.FailureResult("檔案資訊不正確。"));
        }

        var check = await meetingService.CheckMediaUploadAsync(request.MeetingId, request.FileName, request.Size);
        if (!check.Success)
        {
            return BadRequest(ApiResult<UploadSessionDto>.FailureResult(check.Message));
        }

        var job = uploadJobs.Start(userId, request);
        return Ok(ApiResult<UploadSessionDto>.SuccessResult(new UploadSessionDto
        {
            UploadId = job.UploadId,
            ChunkSize = ChunkedUploadStore.ChunkSize,
            ChunkCount = job.ChunkCount,
        }));
    }

    /// <summary>
    /// 專案附件（1.1.24）：先傳到暫存檔，存專案（<c>POST api/v1/Project/form</c>／<c>PUT …/{id}/form</c>）時帶 <c>uploadIds</c>。
    /// 新增專案附檔要「新增」權限、修改時加附件要「修改」權限，所以任一即可；附件規則（封存、資料範圍）在存專案時由服務層把關。
    /// </summary>
    [HttpPost("project-attachments")]
    [HasPermission(MagicObjectHelper.角色_專案項目, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public ActionResult<ApiResult<UploadSessionDto>> CreateProjectAttachment([FromBody] UploadSessionRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<UploadSessionDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (request.Size < 0 || string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest(ApiResult<UploadSessionDto>.FailureResult("檔案資訊不正確。"));
        }

        if (request.Size > ProjectService.MaxUploadFileSize)
        {
            return BadRequest(ApiResult<UploadSessionDto>.FailureResult($"{request.FileName} 超過 1GB 限制"));
        }

        var upload = attachmentUploads.Start(userId, request);
        return Ok(ApiResult<UploadSessionDto>.SuccessResult(new UploadSessionDto
        {
            UploadId = upload.UploadId,
            ChunkSize = ChunkedUploadStore.ChunkSize,
            ChunkCount = upload.ChunkCount,
        }));
    }

    /// <summary>查這趟上傳的狀態（會議影音檔結束後保留 10 分鐘；專案附件保留到存檔或 24 小時）。</summary>
    [HttpGet("{uploadId:guid}")]
    public ActionResult<ApiResult<UploadStatusDto>> Get(Guid uploadId)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<UploadStatusDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (uploadJobs.TryGet(uploadId, userId, out var job))
        {
            return Ok(ApiResult<UploadStatusDto>.SuccessResult(job.ToDto()));
        }

        return attachmentUploads.TryGet(uploadId, userId, out var attachment)
            ? Ok(ApiResult<UploadStatusDto>.SuccessResult(attachment.ToDto()))
            : NotFound(ApiResult<UploadStatusDto>.NotFoundResult("找不到這趟上傳。"));
    }

    /// <summary>取消上傳（使用者按「取消上傳」）：新增時附的檔，會議一起刪。已經寫完的來不及取消，照樣排入轉錄。</summary>
    [HttpDelete("{uploadId:guid}")]
    public ActionResult<ApiResult> Cancel(Guid uploadId)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (uploadJobs.TryGet(uploadId, userId, out var job))
        {
            job.Cancellation.Cancel();
        }
        else if (attachmentUploads.TryGet(uploadId, userId, out var attachment))
        {
            attachment.Cancellation.Cancel();
        }
        else
        {
            return NotFound(ApiResult.NotFoundResult("找不到這趟上傳。"));
        }

        return Ok(ApiResult.SuccessResult("已要求取消上傳。"));
    }

    /// <summary>
    /// 瀏覽器送不下去（1.1.23）：讓服務層走「上傳失敗」流程
    /// （會議保留、原因帶到畫面），不必等 2 分鐘逾時。已經收齊或已結束的不受影響。
    /// </summary>
    [HttpPost("{uploadId:guid}/fail")]
    public ActionResult<ApiResult> Fail(Guid uploadId, [FromBody] UploadFailRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (!uploadJobs.TryGet(uploadId, userId, out _) && !attachmentUploads.TryGet(uploadId, userId, out _))
        {
            return NotFound(ApiResult.NotFoundResult("找不到這趟上傳。"));
        }

        if (store.TryGet(uploadId, userId, out var session))
        {
            var message = string.IsNullOrWhiteSpace(request.Message) ? "上傳失敗，請重新上傳。" : request.Message.Trim();
            session.Fail(message.Length > MaxFailMessageLength ? message[..MaxFailMessageLength] : message);
        }

        return Ok(ApiResult.SuccessResult("已記錄。"));
    }

    /// <summary>原因只拿來顯示給上傳者自己看，仍限制長度。</summary>
    private const int MaxFailMessageLength = 200;
}
