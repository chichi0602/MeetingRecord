using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Layout;

namespace MeetingRecord.Tests;

/// <summary>
/// 「系統管理 → AI 管理」選單（1.0.30）：AI 對話紀錄（63）、Token 用量（33，原「AI 用量分析」）。
/// 兩頁都只限管理者、不在權限目錄裡；群組（62）本身沒有權限。
/// 少了任何一環，選單不會出現或非管理者看得到，但**編譯與其他測試都不會失敗**。
/// </summary>
public sealed class AiManagementMenuRegistrationTests
{
    private const int SystemAdminId = 3;
    private const int MonitoringGroupId = 36;
    private const int AiGroupId = 62;
    private const int AiCallLogId = 63;
    private const int TokenUsageId = 33;

    [Fact]
    public void MenuJson_AiGroupShouldHoldCallLogsThenTokenUsage()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var group = FindNodeById(document.RootElement, AiGroupId);
        Assert.NotNull(group);
        Assert.Equal(MagicObjectHelper.角色_AI管理, group!.Value.GetProperty("name").GetString());
        Assert.False(group.Value.TryGetProperty("url", out _));

        var children = group.Value.GetProperty("subMenu").EnumerateArray().ToList();
        Assert.Equal([AiCallLogId, TokenUsageId], children.Select(x => x.GetProperty("id").GetInt32()));

        // name 是頁面標題（MainLayout 依 Menu.json 顯示），與常數一字不差。
        Assert.Equal(MagicObjectHelper.角色_AI對話紀錄, children[0].GetProperty("name").GetString());
        Assert.Equal("/ai-call-logs", children[0].GetProperty("url").GetString());
        Assert.Equal(MagicObjectHelper.角色_Token用量, children[1].GetProperty("name").GetString());
        Assert.Equal("/token-usage", children[1].GetProperty("url").GetString());
    }

    [Fact]
    public void MenuJson_AiGroupShouldSitUnderSystemAdministration_AndTokenUsageLeftMonitoring()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var system = FindNodeById(document.RootElement, SystemAdminId)!.Value;
        Assert.NotNull(FindNodeById(system.GetProperty("subMenu"), AiGroupId));

        var monitoring = FindNodeById(system.GetProperty("subMenu"), MonitoringGroupId)!.Value;
        var monitoringIds = monitoring.GetProperty("subMenu").EnumerateArray().Select(x => x.GetProperty("id").GetInt32());
        Assert.DoesNotContain(TokenUsageId, monitoringIds);
    }

    [Fact]
    public void Sidebar_BothPagesShouldBeAdminOnly_AndTheGroupGroupOnly()
    {
        Assert.Contains(AiCallLogId, SidebarMenuService.AdminOnlyMenuIds);
        Assert.Contains(TokenUsageId, SidebarMenuService.AdminOnlyMenuIds);
        Assert.Contains(AiGroupId, SidebarMenuService.GroupOnlyMenuIds);
        Assert.False(SidebarMenuService.MenuPermissionMap.ContainsKey(TokenUsageId));
        Assert.False(SidebarMenuService.MenuPermissionMap.ContainsKey(AiCallLogId));
    }

    [Fact]
    public void RolePermissionCatalog_ShouldNotExposeAiManagementOrTheRetiredKey()
    {
        var keys = new RolePermissionService().GetRolePermissionAllName();

        Assert.DoesNotContain(MagicObjectHelper.角色_AI管理, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_AI對話紀錄, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_Token用量, keys);
        Assert.DoesNotContain(MagicObjectHelper.已退役權限鍵_AI用量分析, keys);
    }

    private static JsonElement? FindNodeById(JsonElement element, int id)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindNodeById(child, id);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        if (element.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.GetInt32() == id)
        {
            return element;
        }

        return element.TryGetProperty("subMenu", out var subMenu) ? FindNodeById(subMenu, id) : null;
    }

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
