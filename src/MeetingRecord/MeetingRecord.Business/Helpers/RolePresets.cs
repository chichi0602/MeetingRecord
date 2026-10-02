using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 預設角色目錄（0.4.108）。正式上線時系統是空的，角色管理頁的「建立預設角色」一鍵建出這四個。
///
/// <para>
/// 內容照 2026/10/01 實際使用中的四個角色。角色只管「能做什麼」，不帶團隊。
/// 裸頁面鍵（例如 <see cref="MagicObjectHelper.角色_專案項目"/>）＝該頁全部動作；
/// 「頁面:動作」鍵（<see cref="PermissionKey.For"/>）＝只給那個動作。
/// 群組鍵（專案管理功能、會議管理功能…）也要給，否則那一區的選單不會出現。
/// </para>
///
/// <para>
/// 「一般使用者」同時是新帳號的預設角色，空系統第一次啟動時 <c>DefaultRoleSeeder</c> 就會建好，
/// 初始權限取自這裡（<see cref="RolePermissionService.GetGeneralUserPermissionNames"/>），兩邊不會分岔。
/// 建進去之後就是一般的角色資料，可以自由修改或刪除。
/// </para>
/// </summary>
public static class RolePresets
{
    public static IReadOnlyList<RolePreset> All { get; } =
    [
        new("管理者", new RolePermissionService().GetRolePermissionAllName()),
        new(MagicObjectHelper.預設角色,
        [
            MagicObjectHelper.角色_使用說明,
            MagicObjectHelper.角色_專案管理,
            .. Actions(MagicObjectHelper.角色_專案項目, PermissionActions.View, PermissionActions.Create, PermissionActions.Edit, PermissionActions.Export),
            .. Actions(MagicObjectHelper.角色_待辦事項, PermissionActions.View, PermissionActions.Create, PermissionActions.Edit, PermissionActions.Export),
            MagicObjectHelper.角色_會議管理,
            .. Actions(MagicObjectHelper.角色_會議紀錄, PermissionActions.View, PermissionActions.Create, PermissionActions.Edit, PermissionActions.Export),
            MagicObjectHelper.角色_登出,
        ]),
        new("檢視者",
        [
            MagicObjectHelper.角色_使用說明,
            MagicObjectHelper.角色_專案管理,
            .. Actions(MagicObjectHelper.角色_專案項目, PermissionActions.View),
            .. Actions(MagicObjectHelper.角色_待辦事項, PermissionActions.View),
            MagicObjectHelper.角色_會議管理,
            .. Actions(MagicObjectHelper.角色_會議紀錄, PermissionActions.View),
            MagicObjectHelper.角色_登出,
        ]),
        new("主管",
        [
            MagicObjectHelper.角色_使用說明,
            MagicObjectHelper.角色_專案管理,
            MagicObjectHelper.角色_專案項目,
            MagicObjectHelper.角色_待辦事項,
            MagicObjectHelper.角色_會議管理,
            MagicObjectHelper.角色_會議紀錄,
            MagicObjectHelper.角色_資料定義,
            MagicObjectHelper.角色_分類清單,
            MagicObjectHelper.角色_團隊清單,
            MagicObjectHelper.角色_登出,
        ]),
    ];

    private static IEnumerable<string> Actions(string page, params string[] actions)
        => actions.Select(action => PermissionKey.For(page, action));
}

/// <summary>一個預設角色：名稱與權限鍵。</summary>
public sealed record RolePreset(string Name, IReadOnlyList<string> PermissionKeys);
