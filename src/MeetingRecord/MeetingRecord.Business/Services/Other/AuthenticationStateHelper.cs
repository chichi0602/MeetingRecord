using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 頁面與動作的權限判斷規則（純函式）。Session 選單 API（<c>SessionController</c>）與 WASM 端的 <c>PermissionView</c> 規則一致。
///
/// <para>
/// 只有靜態方法（1.1.46 起）。登入狀態的判斷在 <see cref="UserSessionService"/>（Session API 用）。
/// </para>
/// </summary>
public static class AuthenticationStateHelper
{
    /// <summary>
    /// 進頁面（與看到選單項目）的規則（1.1.0 抽出）：管理者一律通過；有整頁鍵或「頁面:view」就能進。
    /// </summary>
    public static bool CanAccessPage(IReadOnlyCollection<string> permissionKeys, bool isAdmin, string name)
    {
        // 管理員一律通過：GetEffectivePermissionKeysAsync 不含 admin 隱含全通過（回空集合），
        // 故此處需短路，否則管理員選單/頁面會全被隱藏。
        if (isAdmin)
        {
            return true;
        }

        // 有整頁鍵（＝全部動作）或有「頁面:view」就能進頁面（0.4.109）。0.4.108 以前只認整頁鍵，
        // 只勾部分動作的角色（例如檢視者只有 專案項目:view）連頁面與選單都看不到。
        return permissionKeys.Contains(name) || permissionKeys.Contains(PermissionKey.For(name, PermissionActions.View));
    }

    /// <summary>
    /// 某頁面的特定動作（view/create/edit/delete/export）：管理者一律通過；有動作鍵「頁面:動作」或整頁鍵（舊制＝全動作）就通過（1.1.46）。
    /// </summary>
    public static bool CanDoAction(IReadOnlyCollection<string> permissionKeys, bool isAdmin, string page, string action)
        => isAdmin || permissionKeys.Contains(PermissionKey.For(page, action)) || permissionKeys.Contains(page);
}
