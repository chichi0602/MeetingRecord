using MeetingRecord.Business.Helpers;
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

            // 0.4.120 起「監控與診斷」（系統健康監控、日誌檢視、日誌等級設定、系統例外紀錄、資料庫用量）、
            // 1.0.30 起「AI 管理」（AI 對話紀錄、Token 用量——原「AI 用量分析」，以前是這裡的單元素開關）
            // 只限管理者，**刻意不列在這裡**——列了就能勾給角色。舊的「系統健康度」「AI 用量分析」鍵由
            // RbacBackfillService 在啟動時退役清除。
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
                // 團隊清單：團隊＝「誰的資料」，決定誰看得到哪些專案，一般使用者預設不給。
                MagicObjectHelper.角色_團隊清單,
                MagicObjectHelper.角色_提示詞清單,
            ],
            [MagicObjectHelper.角色_登出],
        ];
    }

    /// <summary>
    /// 新建立的「一般使用者」角色的初始權限。0.4.108 起取自 <see cref="RolePresets"/>，
    /// 與角色管理頁「建立預設角色」是同一份：業務頁可以檢視、新增、修改、匯出，不能刪除。
    /// </summary>
    public List<string> GetGeneralUserPermissionNames()
        => [.. RolePresets.All.Single(x => x.Name == MagicObjectHelper.預設角色).PermissionKeys];

    public List<string> GetRolePermissionAllName()
    {
        var result = GetRoleListPermissionAllName()
            .SelectMany(x => x)
            .ToList();

        return result;
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
