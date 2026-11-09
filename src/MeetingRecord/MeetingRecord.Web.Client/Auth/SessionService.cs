using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Web.Client.Auth;

/// <summary>
/// 目前登入者（<c>GET /api/v1/session</c>）與側邊選單（<c>GET /api/v1/session/menu</c>），1.1.1。
/// 狀態整頁只抓一次：版面與 <see cref="SessionAuthenticationStateProvider"/> 共用同一份結果。
/// 判斷規則都在伺服器（UserSessionService），這裡只取結果。
/// </summary>
public sealed class SessionService(ApiClient api)
{
    private Task<ApiResult<SessionDto>>? session;

    public Task<ApiResult<SessionDto>> GetAsync()
        => session ??= api.GetAsync("api/v1/session", ClientJsonContext.Default.SessionResult);

    public Task<ApiResult<List<MenuItemDto>>> GetMenuAsync()
        => api.GetAsync("api/v1/session/menu", ClientJsonContext.Default.MenuResult);
}
