using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Jobs;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 背景工作進度（1.1.15）：WASM 頁右下角的進度面板用；收到 <c>AppHub.JobsChanged</c> 推播後重查。
/// 規則在 <see cref="BackgroundJobBoard"/>：只看得到、也只動得到自己資料範圍內的會議的工作，
/// 看不到的回 404；取消要會議紀錄的修改權限。
/// </summary>
[Route("api/v1/jobs")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class JobController(BackgroundJobBoard board) : ControllerBase
{
    /// <summary>呼叫者看得到的背景工作（含已完成、尚未關閉的）。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResult<List<BackgroundJobDto>>>> Get()
        => Ok(ApiResult<List<BackgroundJobDto>>.SuccessResult(await board.GetVisibleAsync()));

    /// <summary>取消一項進行中的工作。<paramref name="kind"/> 是 <c>Transcription</c>（含上傳中）或 <c>Draft</c>。</summary>
    [HttpPost("{meetingId:int}/cancel")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Cancel(int meetingId, [FromQuery] string kind)
    {
        if (!BackgroundJobKinds.IsValid(kind))
        {
            return BadRequest(ApiResult.FailureResult("工作種類不正確。"));
        }

        return await board.CancelAsync(kind, meetingId)
            ? Ok(ApiResult.SuccessResult("已要求取消。"))
            : NotFound(ApiResult.NotFoundResult("找不到進行中的工作。"));
    }

    /// <summary>關閉一則通知（工作照跑）。</summary>
    [HttpPost("{meetingId:int}/dismiss")]
    public async Task<ActionResult<ApiResult>> Dismiss(int meetingId, [FromQuery] string kind)
    {
        if (!BackgroundJobKinds.IsValid(kind))
        {
            return BadRequest(ApiResult.FailureResult("工作種類不正確。"));
        }

        return await board.DismissAsync(kind, meetingId)
            ? Ok(ApiResult.SuccessResult("已關閉。"))
            : NotFound(ApiResult.NotFoundResult("找不到這項工作。"));
    }

    /// <summary>關閉看得到的全部通知（工作照跑）。</summary>
    [HttpPost("dismiss-all")]
    public async Task<ActionResult<ApiResult<int>>> DismissAll()
        => Ok(ApiResult<int>.SuccessResult(await board.DismissAllAsync()));
}
