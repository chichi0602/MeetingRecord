using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MeetingRecord.Web.Client.Auth;

/// <summary>
/// WASM 端的登入狀態（1.1.1）：依 Session API 的結果建立身分，頁面就能用 <c>AuthorizeView</c>／<c>[Authorize]</c>。
/// ⚠️ 只供畫面決定顯示什麼；真正的權限把關在伺服器每支 API 的 <c>[HasPermission]</c>，前端的判斷可以被使用者改掉。
/// </summary>
public sealed class SessionAuthenticationStateProvider(SessionService sessionService) : AuthenticationStateProvider
{
    public const string PermissionClaimType = "permission";
    public const string IsAdminClaimType = "is_admin";

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var result = await sessionService.GetAsync();
        if (!result.Success || result.Data is not { } session)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Sid, session.UserId.ToString()),
            new(ClaimTypes.NameIdentifier, session.Account),
            new(ClaimTypes.Name, session.Name),
            new(IsAdminClaimType, session.IsAdmin ? "true" : "false"),
        };
        claims.AddRange(session.PermissionKeys.Select(key => new Claim(PermissionClaimType, key)));
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "MeetingRecordSession")));
    }
}
