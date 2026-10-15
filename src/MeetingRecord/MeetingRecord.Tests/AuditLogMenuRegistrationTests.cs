using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Layout;

namespace MeetingRecord.Tests;

/// <summary>
/// 「系統管理 → 帳號與權限 → 稽核紀錄」（1.0.32）。只限管理者、不在權限目錄裡。
/// 少了任何一環，選單不會出現或非管理者看得到，但**編譯與其他測試都不會失敗**。
/// </summary>
public sealed class AuditLogMenuRegistrationTests
{
    private const int AccountGroupId = 35;
    private const int UserManagementId = 31;
    private const int RoleManagementId = 32;
    private const int AuditLogId = 40;

    [Fact]
    public void MenuJson_AuditLogShouldBeTheLastItemUnderAccountGroup()
    {
        var group = Flatten(LoadMenu()).Single(x => x.Id == AccountGroupId);

        Assert.Equal([UserManagementId, RoleManagementId, AuditLogId], group.SubMenu.Select(x => x.Id));
        var node = group.SubMenu[^1];

        // name 是頁面標題（MainLayout 依 Menu.json 顯示），與常數一字不差。
        Assert.Equal(MagicObjectHelper.角色_稽核紀錄, node.Name);
        Assert.Equal("/audit-logs", node.Url);
        Assert.Equal("fact_check", node.Icon);
    }

    [Fact]
    public void Sidebar_AuditLogShouldBeAdminOnly_AndNotInThePermissionCatalog()
    {
        Assert.Contains(AuditLogId, SidebarMenuService.AdminOnlyMenuIds);
        Assert.False(SidebarMenuService.MenuPermissionMap.ContainsKey(AuditLogId));
        Assert.DoesNotContain(MagicObjectHelper.角色_稽核紀錄, new RolePermissionService().GetRolePermissionAllName());
    }

    [Fact]
    public void Filter_NonAdminWithUserManagement_ShouldSeeTheGroupButNotAuditLog()
    {
        // 就算角色的權限清單剛好有同名的「稽核紀錄」，也不能讓非管理者看到。
        var granted = new HashSet<string>
        {
            MagicObjectHelper.角色_系統管理,
            MagicObjectHelper.角色_使用者管理,
            MagicObjectHelper.角色_稽核紀錄,
        };

        var items = SidebarMenuService.FilterAuthorizedMenuItems(
            SidebarMenuService.ApplyPermissionStructure(LoadMenu()),
            granted.Contains,
            isAdmin: false);

        var ids = Flatten(items).Select(x => x.Id).ToList();
        Assert.Contains(UserManagementId, ids);
        Assert.DoesNotContain(AuditLogId, ids);
    }

    [Fact]
    public void Filter_Admin_ShouldSeeAuditLog()
    {
        var items = SidebarMenuService.FilterAuthorizedMenuItems(
            SidebarMenuService.ApplyPermissionStructure(LoadMenu()),
            _ => true,
            isAdmin: true);

        Assert.Contains(Flatten(items), x => x.Id == AuditLogId);
    }

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
