using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Client.Auth;

/// <summary>
/// WASM 頁決定顯示哪些按鈕、能不能進頁面（1.1.7）。資料來自 Session API 的權限鍵。
/// <para>
/// 規則與伺服器一致：進頁面同 <c>AuthenticationStateHelper.CanAccessPage</c>（整頁鍵或「頁:view」），
/// 動作同 <c>AuthenticationStateHelper.CheckAccessAction</c>（動作鍵或整頁鍵），管理者一律通過。
/// ⚠️ 這裡只管畫面；真正的權限檢查在每支 API 的 <c>[HasPermission]</c>。
/// </para>
/// </summary>
public sealed class PermissionView(SessionService sessionService)
{
    private IReadOnlyCollection<string> keys = [];
    private bool isAdmin;

    /// <summary>讀目前登入者的權限（Session 整頁只抓一次，重複呼叫不會多打 API）。</summary>
    public async Task LoadAsync()
    {
        var result = await sessionService.GetAsync();
        keys = result.Data?.PermissionKeys.ToHashSet(StringComparer.Ordinal) ?? [];
        isAdmin = result.Data?.IsAdmin ?? false;
    }

    /// <summary>是不是管理者（1.1.10）：使用者管理、角色管理這類只限管理者的頁面只看這個，同 Server 頁的 <c>CheckIsAdmin</c>。</summary>
    public bool IsAdmin => isAdmin;

    public bool CanAccessPage(string page) => CanAccessPage(keys, isAdmin, page);

    public bool Can(string page, string action) => Can(keys, isAdmin, page, action);

    public static bool CanAccessPage(IReadOnlyCollection<string> permissionKeys, bool isAdmin, string page)
        => isAdmin || permissionKeys.Contains(page) || permissionKeys.Contains(PermissionKey.For(page, PermissionActions.View));

    public static bool Can(IReadOnlyCollection<string> permissionKeys, bool isAdmin, string page, string action)
        => isAdmin || permissionKeys.Contains(PermissionKey.For(page, action)) || permissionKeys.Contains(page);
}
