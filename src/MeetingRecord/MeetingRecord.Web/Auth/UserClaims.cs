using System.Security.Claims;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// 從登入身分取出使用者 Id（1.1.0）。
///
/// <para>
/// ⚠️ 兩種登入放 Id 的地方不一樣：網頁登入 Cookie 的 <see cref="ClaimTypes.NameIdentifier"/> 是**帳號字串**、Id 在 <see cref="ClaimTypes.Sid"/>；
/// JWT 的 Id 在 <see cref="ClaimTypes.NameIdentifier"/>、沒有 Sid。以前 API 只讀 NameIdentifier，用 Cookie 打 API 一律被當成未登入。
/// 先看 Sid，再看「是數字的」NameIdentifier。
/// </para>
/// </summary>
public static class UserClaims
{
    public static int? GetUserId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        foreach (var type in new[] { ClaimTypes.Sid, ClaimTypes.NameIdentifier })
        {
            if (int.TryParse(principal.FindFirstValue(type), out var id) && id > 0)
            {
                return id;
            }
        }

        return null;
    }
}
