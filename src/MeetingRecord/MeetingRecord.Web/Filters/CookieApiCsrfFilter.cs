using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// Web API 的 CSRF 防護（1.1.0，WASM 遷移 M1）：用登入 Cookie 驗證、會改資料（不是 GET／HEAD／OPTIONS／TRACE）的請求，
/// 一定要帶自訂標頭 <see cref="MagicObjectHelper.ClientRequestHeader"/>，否則回 403 <see cref="ApiResult"/>。
///
/// <para>
/// 為什麼擋得住：瀏覽器會自動帶 Cookie，但別的網站要帶自訂標頭得先過 CORS 預檢，而 CORS 沒開放任何網域。
/// 帶 Bearer 權杖的請求（外部用戶端）不靠 Cookie，不受影響；匿名請求也不受影響。
/// 自己就檢查防偽標頭的端點標 <see cref="OwnCsrfCheckAttribute"/>，這裡跳過（上傳分塊端點，1.0.60）。
/// </para>
/// </summary>
public sealed class CookieApiCsrfFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<OwnCsrfCheckAttribute>().Any())
        {
            return;
        }

        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        {
            return;
        }

        if (request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (request.Headers.ContainsKey(MagicObjectHelper.ClientRequestHeader))
        {
            return;
        }

        context.Result = new ObjectResult(ApiResult.ForbiddenResult($"缺少防偽標頭 {MagicObjectHelper.ClientRequestHeader}。"))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}

/// <summary>
/// 這個端點自己檢查防偽標頭，<see cref="CookieApiCsrfFilter"/> 跳過（1.1.0）。
/// 目前只有上傳分塊端點：它要求 <c>X-MeetingRecord-Upload</c>、缺了回 400（1.0.60 起的行為，瀏覽器端腳本依此判斷）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OwnCsrfCheckAttribute : Attribute;
