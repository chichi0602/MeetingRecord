using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 守住「AI 用量分析」頁面的宣告式權限註冊三件組：
/// <c>MagicObjectHelper</c> 權限鍵常數、<c>Menu.json</c> 節點、以及 <c>RolePermissionService</c> 權限目錄。
/// 少了任何一環，選單不會出現或權限矩陣勾不到，但**編譯與其他測試都不會失敗**。
/// </summary>
public sealed class AiUsageRegistrationTests
{
    private const int AiUsageMenuId = 33;
    private const string AiUsageUrl = "/ai-usage";

    [Fact]
    public void RolePermissionCatalog_ShouldContainAiUsagePage()
    {
        var service = new RolePermissionService();

        Assert.Contains(MagicObjectHelper.角色_AI用量分析, service.GetRolePermissionAllName());
    }

    [Fact]
    public void RolePermissionCatalog_ShouldExposeAiUsageAsASingleSwitch()
    {
        // 純閱讀頁用單元素群組（比照使用說明）。
        // 塞進「系統管理」那一組的話，Skip(1) 的成員會長出 create/edit/delete/export
        // 勾選欄，對一個只能看的頁面沒有意義。
        var service = new RolePermissionService();

        var group = service.GetRoleListPermissionAllName()
            .Single(x => x.Contains(MagicObjectHelper.角色_AI用量分析));

        Assert.Single(group);
    }

    [Fact]
    public void MenuJson_ShouldContainAiUsageNode()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var node = FindNodeById(document.RootElement, AiUsageMenuId);

        Assert.NotNull(node);

        // ⚠️ name 必須與權限鍵**一字不差**：SidebarMenuService 會同時拿 Name 與
        // PermissionName 去比對，對不上的話非管理員一律看不到這一頁。
        Assert.Equal(MagicObjectHelper.角色_AI用量分析, node!.Value.GetProperty("name").GetString());
        Assert.Equal(AiUsageUrl, node.Value.GetProperty("url").GetString());
    }

    [Fact]
    public void MenuJson_ShouldPlaceAiUsageUnderSystemAdministration()
    {
        // 這一頁是管理員限定，放在「系統管理」底下與使用者管理、角色管理同層。
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var systemGroup = FindNodeById(document.RootElement, 3);
        Assert.NotNull(systemGroup);

        var ids = systemGroup!.Value.GetProperty("subMenu")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetInt32())
            .ToList();

        Assert.Contains(AiUsageMenuId, ids);
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

        if (element.TryGetProperty("subMenu", out var subMenu))
        {
            return FindNodeById(subMenu, id);
        }

        return null;
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
