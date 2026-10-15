using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Layout;

namespace MeetingRecord.Tests;

/// <summary>
/// 「系統管理 → 帳號與權限／監控與診斷」三層選單與管理者限定（0.4.120）。
///
/// <para>管理者限定的頁面不在權限目錄裡，側邊欄靠 <see cref="SidebarMenuService.AdminOnlyMenuIds"/> 排除非管理者——
/// 不看權限鍵，所以 0.4.119 以前勾過「系統健康度」的舊角色也開不了門。</para>
/// </summary>
public sealed class MonitoringMenuRegistrationTests
{
    private const int SystemAdminId = 3;
    private const int AccountGroupId = 35;
    private const int MonitoringGroupId = 36;
    private const int SystemHealthId = 34;
    private const int AiGroupId = 62;
    private const int TokenUsageId = 33;

    [Fact]
    public void MenuJson_IdsShouldBeUniqueAcrossTheWholeTree()
    {
        var ids = Flatten(LoadMenu()).Select(x => x.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void MenuJson_SystemAdministrationShouldHaveThreeGroups()
    {
        // 1.0.30 起多了「AI 管理」（62）。
        var system = LoadMenu().Single(x => x.Id == SystemAdminId);

        Assert.Equal([AccountGroupId, MonitoringGroupId, AiGroupId], system.SubMenu.Select(x => x.Id));
        Assert.Equal(MagicObjectHelper.角色_帳號與權限, system.SubMenu[0].Name);
        Assert.Equal(MagicObjectHelper.角色_監控與診斷, system.SubMenu[1].Name);
        Assert.Equal(MagicObjectHelper.角色_AI管理, system.SubMenu[2].Name);
        Assert.All(system.SubMenu, group => Assert.True(string.IsNullOrEmpty(group.Url)));
    }

    [Theory]
    [InlineData(34, MagicObjectHelper.角色_系統健康監控, "/system-health")]
    [InlineData(37, MagicObjectHelper.角色_日誌檢視, "/logs")]
    [InlineData(38, MagicObjectHelper.角色_日誌等級設定, "/log-level-setting")]
    [InlineData(39, MagicObjectHelper.角色_系統例外紀錄, "/system-exceptions")]
    [InlineData(30, MagicObjectHelper.角色_資料庫用量, "/database-usage")]
    public void MenuJson_AdminOnlyPagesShouldSitUnderMonitoringGroup(int id, string name, string url)
    {
        var monitoring = Flatten(LoadMenu()).Single(x => x.Id == MonitoringGroupId);
        var node = monitoring.SubMenu.Single(x => x.Id == id);

        // name 是頁面標題（MainLayout 依 Menu.json 顯示），與常數一字不差。
        Assert.Equal(name, node.Name);
        Assert.Equal(url, node.Url);
        Assert.Contains(id, SidebarMenuService.AdminOnlyMenuIds);
    }

    [Fact]
    public void AdminOnlyPages_ShouldNotBeInRolePermissionCatalog()
    {
        var catalog = new RolePermissionService().GetRolePermissionAllName();
        var menu = Flatten(LoadMenu()).ToList();

        foreach (var id in SidebarMenuService.AdminOnlyMenuIds)
        {
            var name = menu.Single(x => x.Id == id).Name;
            Assert.DoesNotContain(name, catalog);
            Assert.False(SidebarMenuService.MenuPermissionMap.ContainsKey(id), $"管理者限定項目 {id} 不應有權限鍵對應。");
        }
    }

    [Fact]
    public void Filter_AdminShouldSeeEverything()
    {
        var items = Filter(isAdmin: true, _ => true);

        var monitoring = Flatten(items).Single(x => x.Id == MonitoringGroupId);
        Assert.Contains(monitoring.SubMenu, x => x.Id == SystemHealthId);
        Assert.Contains(Flatten(items).Single(x => x.Id == AiGroupId).SubMenu, x => x.Id == TokenUsageId);
        Assert.Contains(Flatten(items), x => x.Id == AccountGroupId);
    }

    [Fact]
    public void Filter_NonAdminWithStaleLegacyPermission_ShouldNotSeeAdminOnlyPages()
    {
        // 0.4.119 以前「系統健康度」可以勾給角色；即使退役清理沒跑到、權限還在，也不能讓選單冒出來。
        var granted = new HashSet<string>
        {
            MagicObjectHelper.已退役權限鍵_系統健康度,
            MagicObjectHelper.角色_系統健康監控,
            MagicObjectHelper.角色_日誌檢視,
            MagicObjectHelper.角色_日誌等級設定,
            MagicObjectHelper.角色_系統例外紀錄,
            MagicObjectHelper.角色_資料庫用量,
            MagicObjectHelper.角色_監控與診斷,
        };

        var items = Filter(isAdmin: false, granted.Contains);

        var ids = Flatten(items).Select(x => x.Id).ToList();
        Assert.DoesNotContain(SystemHealthId, ids);
        Assert.DoesNotContain(37, ids);
        Assert.DoesNotContain(39, ids);
        Assert.DoesNotContain(38, ids);
        Assert.DoesNotContain(30, ids);
        // 底下沒有任何看得到的項目，群組與「系統管理」也要一起藏起來。
        Assert.DoesNotContain(MonitoringGroupId, ids);
        Assert.DoesNotContain(SystemAdminId, ids);
    }

    [Fact]
    public void Filter_NonAdminWithStaleAiUsagePermission_ShouldSeeNothingUnderSystemAdministration()
    {
        // 0.4.80～1.0.29「AI 用量分析」可以勾給角色；1.0.30 起改名 Token 用量並只限管理者。
        // 退役清理沒跑到、舊權限還在的角色也不能讓選單冒出來。
        var items = Filter(isAdmin: false, key => key == MagicObjectHelper.已退役權限鍵_AI用量分析
                                                  || key == MagicObjectHelper.角色_Token用量
                                                  || key == MagicObjectHelper.角色_AI對話紀錄);

        Assert.DoesNotContain(items, x => x.Id == SystemAdminId);
    }

    [Fact]
    public void Filter_GroupNameAsPermissionKey_ShouldNotRevealEmptyGroup()
    {
        // 群組本身沒有權限：就算有人的權限清單剛好有同名的鍵，底下沒東西也不能顯示空群組。
        var items = Filter(isAdmin: false, key => key == MagicObjectHelper.角色_帳號與權限);

        Assert.DoesNotContain(Flatten(items), x => x.Id == AccountGroupId);
    }

    private static List<SidebarMenuItemModel> Filter(bool isAdmin, Func<string, bool> canAccessPage)
        => SidebarMenuService.FilterAuthorizedMenuItems(
            SidebarMenuService.ApplyPermissionStructure(LoadMenu()),
            key => isAdmin || canAccessPage(key),
            isAdmin);

    private static IEnumerable<SidebarMenuItemModel> Flatten(IEnumerable<SidebarMenuItemModel> items)
        => items.SelectMany(x => new[] { x }.Concat(Flatten(x.SubMenu)));

    private static List<SidebarMenuItemModel> LoadMenu()
        => JsonSerializer.Deserialize<List<SidebarMenuItemModel>>(
            File.ReadAllText(FindMenuJsonPath()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    private static string FindMenuJsonPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MeetingRecord.Web", "Datas", "Menu.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "MeetingRecord", "MeetingRecord.Web", "Datas", "Menu.json");
            if (File.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 MeetingRecord.Web/Datas/Menu.json。");
    }
}
