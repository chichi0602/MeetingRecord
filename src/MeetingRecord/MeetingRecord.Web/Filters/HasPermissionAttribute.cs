using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// API 功能級授權：要求目前使用者具備指定權限鍵，否則回傳 ApiResult 403。
/// 與 UI 共用 <see cref="IPermissionChecker"/> 作為權限判定來源。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string permissionKey;

    /// <summary>頁面級（裸鍵）授權。</summary>
    public HasPermissionAttribute(string permissionKey)
    {
        this.permissionKey = permissionKey;
    }

    /// <summary>動作級授權，權限鍵為「頁面:動作」。</summary>
    public HasPermissionAttribute(string page, string action)
    {
        this.permissionKey = PermissionKey.For(page, action);
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("尚未登入或憑證無效。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idValue, out var userId))
        {
            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("無法識別使用者身分。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var checker = context.HttpContext.RequestServices.GetRequiredService<IPermissionChecker>();
        if (!await checker.HasPermissionAsync(userId, permissionKey))
        {
            context.Result = new ObjectResult(ApiResult.ForbiddenResult($"目前使用者沒有「{permissionKey}」權限。"))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
