using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// API 功能級授權：要求目前使用者具備指定權限鍵，否則回傳 ApiResult 403。
/// 與 UI 共用 <see cref="IPermissionChecker"/> 作為權限判定來源。
/// 1.0.33 起回 403 時寫一筆 <see cref="AuditActions.PermissionDenied"/>（401 未登入不記）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>有其中任何一個就通過（一般只有一個）。</summary>
    private readonly string[] permissionKeys;

    /// <summary>頁面級（裸鍵）授權。</summary>
    public HasPermissionAttribute(string permissionKey)
    {
        permissionKeys = [permissionKey];
    }

    /// <summary>動作級授權，權限鍵為「頁面:動作」。</summary>
    public HasPermissionAttribute(string page, string action)
    {
        permissionKeys = [PermissionKey.For(page, action)];
    }

    /// <summary>
    /// 任一動作即可（1.1.8）：例如團隊的可選成員清單，能新增**或**能修改團隊的人都要用到（編輯視窗裡的下拉）。
    /// 寫法：<c>[HasPermission(頁, new[] { PermissionActions.Create, PermissionActions.Edit })]</c>。
    /// </summary>
    public HasPermissionAttribute(string page, string[] anyOf)
    {
        if (anyOf.Length == 0)
        {
            throw new ArgumentException("至少要指定一個動作。", nameof(anyOf));
        }

        permissionKeys = anyOf.Select(action => PermissionKey.For(page, action)).ToArray();
    }

    /// <summary>給訊息與稽核用：「團隊清單:create 或 團隊清單:edit」。</summary>
    private string Describe() => string.Join(" 或 ", permissionKeys);

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

        // 1.1.0：Cookie（Id 在 Sid）與 JWT（Id 在 NameIdentifier）都認得，見 UserClaims。
        if (UserClaims.GetUserId(principal) is not { } userId)
        {
            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("無法識別使用者身分。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var checker = context.HttpContext.RequestServices.GetRequiredService<IPermissionChecker>();
        foreach (var key in permissionKeys)
        {
            if (await checker.HasPermissionAsync(userId, key))
            {
                return;
            }
        }

        context.Result = new ObjectResult(ApiResult.ForbiddenResult($"目前使用者沒有「{string.Join("」或「", permissionKeys)}」權限。"))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };

        await WriteDeniedAuditAsync(context, principal, userId);
    }

    /// <summary>⚠️ 稽核寫不進去也要照樣回 403：這裡任何例外都吞掉、只記警告。</summary>
    private async Task WriteDeniedAuditAsync(AuthorizationFilterContext context, ClaimsPrincipal principal, int userId)
    {
        try
        {
            var (account, _) = RequestActorResolver.Resolve(principal);
            var request = context.HttpContext.Request;
            var audit = context.HttpContext.RequestServices.GetRequiredService<IAuditLogService>();
            await audit.WriteAsync(
                AuditActions.PermissionDenied,
                success: false,
                actorUserId: userId,
                actorAccount: account,
                targetType: "ApiEndpoint",
                targetId: $"{request.Method} {request.Path}",
                detail: $"缺少權限：{Describe()}; via={AuditVia.Api}");
        }
        catch (Exception ex)
        {
            context.HttpContext.RequestServices.GetService<ILogger<HasPermissionAttribute>>()
                ?.LogWarning(ex, "Failed to write permission-denied audit. Permission={Permission}", Describe());
        }
    }
}
