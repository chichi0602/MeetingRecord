using MeetingRecord.Models.Admins;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

public class RolePermissionService
{
    public List<List<string>> GetRoleListPermissionAllName()
    {
        return
        [
            // 儀表板 0.4.97 起登入即可看，不再列在這裡（見 SidebarMenuService.PublicMenuIds）。
            //
            // 單元素＝只有一個開關、沒有動作欄。使用說明是純閱讀頁，
            // ⚠️ 但仍然要勾才看得到——新建角色時記得把它打開。
            [MagicObjectHelper.角色_使用說明],

            // AI 用量分析（0.4.80）。同樣是純閱讀頁，所以是單元素而不是塞進「系統管理」那一組
            // ——那組的 Skip(1) 成員會長出 create/edit/delete/export 勾選欄，對唯讀頁沒有意義。
            // ⚠️ 這一頁看得到**全公司**的用量與人名，而且不套團隊過濾。
            //    預設只給管理員；勾給其他角色之前請先確認那是刻意的。
            [MagicObjectHelper.角色_AI用量分析],

            // 系統健康度（0.4.93）。唯讀頁，理由同上用單元素群組。預設只給管理員；
            // 開給其他角色時，頁面下方未遮罩的原始日誌仍然只有管理員看得到。
            [MagicObjectHelper.角色_系統健康度],
            [
                MagicObjectHelper.角色_專案管理,
                MagicObjectHelper.角色_專案項目,
                MagicObjectHelper.角色_待辦事項,
            ],
            [
                MagicObjectHelper.角色_會議管理,
                MagicObjectHelper.角色_會議紀錄,
            ],
            [
                MagicObjectHelper.角色_系統管理,
                MagicObjectHelper.角色_使用者管理,
                MagicObjectHelper.角色_角色管理,
            ],
            [
                MagicObjectHelper.角色_資料定義,
                MagicObjectHelper.角色_分類清單,
                MagicObjectHelper.角色_團隊清單,
                MagicObjectHelper.角色_提示詞清單,
            ],
            [MagicObjectHelper.角色_登出],
        ];
    }

    public List<string> GetRolePermissionAllName()
    {
        var result = GetRoleListPermissionAllName()
            .SelectMany(x => x)
            .ToList();

        return result;
    }

    public string GetRolePermissionAllNameToJson()
    {
        var items = GetRolePermissionAllName();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
        return json;
    }

    public RolePermission InitializePermissionSetting()
    {
        var result = new RolePermission();

        foreach (var permissionNames in GetRoleListPermissionAllName())
        {
            if (permissionNames.Count == 0)
            {
                continue;
            }

            var group = new RolePermissionGroup
            {
                Name = permissionNames[0],
                Enable = false,
            };

            foreach (var item in permissionNames.Skip(1))
            {
                group.Permissions.Add(new RolePermissionNode
                {
                    Name = item,
                    Enable = false,
                    Actions = CreateEmptyActions(),
                });
            }

            result.Groups.Add(group);
        }

        return result;
    }

    /// <summary>權限矩陣支援的動作代碼（顯示順序）。</summary>
    public static readonly IReadOnlyList<string> SupportedActions =
    [
        PermissionActions.View,
        PermissionActions.Create,
        PermissionActions.Edit,
        PermissionActions.Delete,
        PermissionActions.Export,
    ];

    private static Dictionary<string, bool> CreateEmptyActions()
        => SupportedActions.ToDictionary(action => action, _ => false, StringComparer.Ordinal);

    public void SetPermissionInput(RolePermission rolePermission, List<string> permissions)
    {
        var permissionLookup = permissions.ToHashSet(StringComparer.Ordinal);

        foreach (var group in rolePermission.Groups)
        {
            group.Enable = permissionLookup.Contains(group.Name);

            foreach (var item in group.Permissions)
            {
                item.Enable = permissionLookup.Contains(item.Name);

                item.Actions ??= CreateEmptyActions();
                foreach (var action in SupportedActions)
                {
                    item.Actions[action] = permissionLookup.Contains(PermissionKey.For(item.Name, action));
                }
            }
        }
    }

    public List<string> GetPermissionInput(RolePermission rolePermission)
    {
        var result = new List<string>();

        foreach (var group in rolePermission.Groups)
        {
            if (group.Enable)
            {
                result.Add(group.Name);
            }

            foreach (var node in group.Permissions)
            {
                if (node.Enable)
                {
                    // 裸頁面鍵＝該頁全部動作（舊制相容）。
                    result.Add(node.Name);
                    continue;
                }

                foreach (var action in SupportedActions)
                {
                    if (node.Actions is not null && node.Actions.TryGetValue(action, out var enabled) && enabled)
                    {
                        result.Add(PermissionKey.For(node.Name, action));
                    }
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public string GetPermissionInputToJson(RolePermission rolePermission)
    {
        var items = GetPermissionInput(rolePermission);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
        return json;
    }
}
