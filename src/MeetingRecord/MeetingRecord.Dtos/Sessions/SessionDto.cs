namespace MeetingRecord.Dtos.Sessions;

/// <summary>
/// 目前登入者的狀態（<c>GET /api/v1/session</c>，1.1.0）：WASM 前端據此決定導向與顯示。
/// 權限判斷的權威仍在伺服器（每支 API 的 <c>[HasPermission]</c>），這裡的權限鍵只供畫面決定顯示哪些按鈕。
/// </summary>
public class SessionDto
{
    /// <summary>
    /// <c>active</c>＝可以用；被擋下時是登出原因（<c>deleted</c>／<c>disabled</c>／<c>no-role</c>），
    /// 前端導到 <c>/Auths/Logout?reason=…</c>。
    /// </summary>
    public string Status { get; set; } = SessionStatuses.Active;

    public int UserId { get; set; }

    public string Account { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    /// <summary>要先改密碼（前端導到 <c>/ChangePassword</c>）。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>系統預設的維護帳號（不能在網頁改密碼）。</summary>
    public bool IsSupportAccount { get; set; }

    /// <summary>有效權限鍵（多角色聯集）；管理者為空集合、一律視為全部通過。</summary>
    public List<string> PermissionKeys { get; set; } = [];
}

public static class SessionStatuses
{
    public const string Active = "active";
}
