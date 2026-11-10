using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// 須改密碼的帳號，除了少數例外端點，Web API 一律回 403（1.1.4，WASM 遷移）。
///
/// <para>
/// 網頁（Blazor Server）靠 <c>AuthenticationStateHelper.Check</c> 把這種人導去改密碼頁；但 WASM 前端用登入 Cookie 直接打 API，
/// 以前不擋，等於繞過「第一次登入先改密碼」。JWT 也一樣擋（權杖發出後管理者才重設密碼的情況）。
/// </para>
///
/// <para>
/// 只讀資料庫的 <c>MustChangePassword</c> 標記，不驗密碼雜湊（太慢）；「仍是預設密碼」在登入時就記成這個標記（<c>MyUserServiceLogin</c>）。
/// 例外端點標 <see cref="AllowWhenMustChangePasswordAttribute"/>（Session：前端要靠它知道該導去改密碼；之後的改密碼 API）。
/// 403 的 <c>Errors["Reason"]</c> 是 <see cref="Reason"/>，前端據此導到 <c>/ChangePassword</c>。
/// </para>
/// </summary>
public sealed class MustChangePasswordFilter : IAsyncAuthorizationFilter
{
    public const string Reason = "must-change-password";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowWhenMustChangePasswordAttribute>().Any()
            || UserClaims.GetUserId(context.HttpContext.User) is not { } userId)
        {
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<BackendDBContext>();
        var mustChange = await db.MyUser.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.MustChangePassword)
            .FirstOrDefaultAsync();
        if (!mustChange)
        {
            return;
        }

        var result = ApiResult.ForbiddenResult("請先變更密碼才能繼續使用。");
        result.Errors = new Dictionary<string, string[]> { ["Reason"] = [Reason] };
        context.Result = new ObjectResult(result) { StatusCode = StatusCodes.Status403Forbidden };
    }
}

/// <summary>須改密碼的帳號也能呼叫這個端點（見 <see cref="MustChangePasswordFilter"/>）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenMustChangePasswordAttribute : Attribute;
