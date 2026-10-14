using System.Security.Claims;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// 從 HTTP 請求的 <see cref="ClaimsPrincipal"/> 取出「帳號、使用者編號」（0.4.122，系統例外紀錄用；移植自 NET10-Blazor-Starter）。
///
/// <para>⚠️ 兩套驗證的 claim 對應相反：
/// Cookie：<c>Sid</c>＝編號、<c>NameIdentifier</c>＝帳號、<c>Name</c>＝姓名（個資，不取用）；
/// JWT（<c>JwtTokenService</c>）：<c>NameIdentifier</c>＝編號、<c>Name</c>＝帳號。
/// 判斷方式比照 <c>RecordAccessScopeProvider</c>：先看 Sid，沒有再把 NameIdentifier 當編號。</para>
/// </summary>
public static class RequestActorResolver
{
    public static (string? Account, int? UserId) Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        var nameIdentifier = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (int.TryParse(principal.FindFirst(ClaimTypes.Sid)?.Value, out var cookieUserId) && cookieUserId > 0)
        {
            return (nameIdentifier, cookieUserId);
        }

        if (int.TryParse(nameIdentifier, out var jwtUserId) && jwtUserId > 0)
        {
            return (principal.FindFirst(ClaimTypes.Name)?.Value, jwtUserId);
        }

        return (null, null);
    }
}
