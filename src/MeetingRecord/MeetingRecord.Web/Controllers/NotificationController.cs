using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 站內通知（1.1.2 未讀數；1.1.6 清單與標已讀，WASM 頁的通知鈴用）。WASM 前端收到 <c>AppHub.NotificationsChanged</c> 推播後重查。
/// 只需登入、只看也只動呼叫者自己的通知。
/// </summary>
[Route("api/v1/notifications")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class NotificationController(UserNotificationService notificationService) : ControllerBase
{
    [HttpGet("unread-count")]
    public async Task<ActionResult<ApiResult<int>>> GetUnreadCount()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<int>.UnauthorizedResult("無法識別使用者身分。"));
        }

        return Ok(ApiResult<int>.SuccessResult(await notificationService.GetUnreadCountAsync(userId)));
    }

    /// <summary>最近的通知（新到舊，未讀已讀都列），最多 20 則。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResult<List<NotificationDto>>>> GetRecent()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<List<NotificationDto>>.UnauthorizedResult("無法識別使用者身分。"));
        }

        var items = await notificationService.GetRecentAsync(userId, 20);
        return Ok(ApiResult<List<NotificationDto>>.SuccessResult(
            items.Select(x => new NotificationDto { Id = x.Id, Message = x.Message, CreatedAt = x.CreatedAt, ReadAt = x.ReadAt, TodoId = x.TodoId }).ToList()));
    }

    /// <summary>標一則已讀；不是自己的通知不動（照樣回成功，不透露那一則是否存在）。</summary>
    [HttpPost("{id:int}/read")]
    public async Task<ActionResult<ApiResult>> MarkRead(int id)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        await notificationService.MarkReadAsync(userId, id);
        return Ok(ApiResult.SuccessResult());
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<ApiResult>> MarkAllRead()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        await notificationService.MarkAllReadAsync(userId);
        return Ok(ApiResult.SuccessResult());
    }
}
