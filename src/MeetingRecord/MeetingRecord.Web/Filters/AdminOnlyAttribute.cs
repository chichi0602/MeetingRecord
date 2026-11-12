using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// 只限管理者的 API（1.1.10）：使用者管理、角色管理這類頁面只看「是不是管理者」，不看權限鍵
/// （頁面用 <c>PermissionView.IsAdmin</c>、選單在 <c>SidebarMenuService.AdminOnlyMenuIds</c>）。
/// <para>
/// 每次都查資料庫：啟用中而且 <c>IsAdmin</c> 才通過（停用的管理者手上的權杖也不能用，比照 <see cref="IPermissionChecker"/>）。
/// 未登入 401；不是管理者 403 並寫一筆 <see cref="AuditActions.PermissionDenied"/>。可以代替 <see cref="HasPermissionAttribute"/>，
/// <c>ApiAuthorizationConventionTests</c> 認得它。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
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

        if (UserClaims.GetUserId(principal) is not { } userId)
        {
            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("無法識別使用者身分。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<BackendDBContext>();
        var isAdmin = await db.MyUser.AsNoTracking().AnyAsync(x => x.Id == userId && x.Status && x.IsAdmin);
        if (isAdmin)
        {
            return;
        }

        context.Result = new ObjectResult(ApiResult.ForbiddenResult("這個功能只限管理者使用。"))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };

        await WriteDeniedAuditAsync(context, principal, userId);
    }

    /// <summary>⚠️ 稽核寫不進去也要照樣回 403：這裡任何例外都吞掉、只記警告。</summary>
    private static async Task WriteDeniedAuditAsync(AuthorizationFilterContext context, ClaimsPrincipal principal, int userId)
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
                detail: $"缺少權限：管理者; via={AuditVia.Api}");
        }
        catch (Exception ex)
        {
            context.HttpContext.RequestServices.GetService<ILogger<AdminOnlyAttribute>>()
                ?.LogWarning(ex, "Failed to write admin-only denied audit.");
        }
    }
}
