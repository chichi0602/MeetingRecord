using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// WASM 頁寫的稽核（1.1.6，WASM 遷移）。Server 頁擋下使用者時呼叫 <c>AuthenticationStateHelper.RecordPageAccessDeniedAsync</c>；
/// WASM 頁在瀏覽器裡判斷，改打這支 API，寫出來的紀錄格式相同（目標 <c>Page#路由</c>），只有 <c>via=Wasm</c> 不同。
/// </summary>
[Route("api/v1/audit")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed partial class AuditController(IAuditLogService auditLogService) : ControllerBase
{
    /// <summary>記一筆「頁面拒絕存取」。路由只收 <c>/</c> 開頭的單純路徑，免得任意內容寫進稽核。</summary>
    [HttpPost("page-denied")]
    public async Task<ActionResult<ApiResult>> PageDenied([FromBody] PageDeniedRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        if (string.IsNullOrEmpty(request.Route) || request.Route.Length > 200 || !RoutePattern().IsMatch(request.Route))
        {
            return BadRequest(ApiResult.FailureResult("路由格式不正確。"));
        }

        var (account, _) = RequestActorResolver.Resolve(User);
        await auditLogService.WriteAsync(
            AuditActions.PermissionDenied,
            success: false,
            actorUserId: userId,
            actorAccount: account,
            targetType: "Page",
            targetId: request.Route,
            detail: $"via={AuditVia.Wasm}");
        return Ok(ApiResult.SuccessResult());
    }

    [GeneratedRegex(@"^/[A-Za-z0-9/_\-]*$")]
    private static partial Regex RoutePattern();
}
