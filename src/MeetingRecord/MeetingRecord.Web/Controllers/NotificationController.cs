using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 站內通知（1.1.2，WASM 遷移 M1）：目前只有未讀數，給 WASM 前端收到 <c>AppHub.NotificationsChanged</c> 推播後重查。
/// 只需登入、只看呼叫者自己的通知。通知清單與標已讀等 M3 搬通知鈴時再開。
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
}
