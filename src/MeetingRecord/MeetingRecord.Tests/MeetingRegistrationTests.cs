using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 守住「會議紀錄」頁面的宣告式權限註冊三件組：
/// <c>MagicObjectHelper</c> 權限鍵常數、<c>Menu.json</c> 節點、以及 <c>RolePermissionService</c> 權限目錄。
/// 少了任何一環，選單不會出現或權限矩陣勾不到，但編譯與其他測試都不會失敗。
/// </summary>
public sealed class MeetingRegistrationTests
{
    private const int MeetingMenuId = 61;
    private const string MeetingUrl = "/meetings";
    private const string CoreSectionName = "核心功能";

    [Fact]
    public void RolePermissionCatalog_ShouldContainMeetingPage()
    {
        var service = new RolePermissionService();

        Assert.Contains(MagicObjectHelper.角色_會議紀錄, service.GetRolePermissionAllName());
    }

    [Fact]
    public void RolePermissionCatalog_ShouldPlaceMeetingUnderMeetingManagementGroup()
    {
        var service = new RolePermissionService();

        var meetingGroup = service.GetRoleListPermissionAllName()
            .Single(group => group.Count > 0 && group[0] == MagicObjectHelper.角色_會議管理);

        Assert.Contains(MagicObjectHelper.角色_會議紀錄, meetingGroup);
    }

    [Fact]
    public void MenuJson_ShouldContainMeetingNode()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var node = FindNodeById(document.RootElement, MeetingMenuId);

        Assert.NotNull(node);
        Assert.Equal(MagicObjectHelper.角色_會議紀錄, node!.Value.GetProperty("name").GetString());
        Assert.Equal(MeetingUrl, node.Value.GetProperty("url").GetString());
    }

    [Fact]
    public void MenuJson_ShouldPlaceMeetingAtTopLevelInCoreSection()
    {
        // 0.4.33 起「會議管理」群組已移除，會議紀錄提升為頂層並歸入「核心功能」區塊。
        // 權限矩陣仍保留 角色_會議管理 群組（見上方 Fact）——選單結構與權限矩陣是獨立的兩件事。
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var topLevel = document.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("id").GetInt32() == MeetingMenuId);

        Assert.False(topLevel.TryGetProperty("subMenu", out _));
        Assert.Equal(CoreSectionName, topLevel.GetProperty("section").GetString());
    }

    [Fact]
    public void MenuJson_EveryTopLevelNodeShouldDeclareSection()
    {
        // 漏標 section 的節點會靜默落到預設區塊，畫面上看不出是設定漏了。
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        foreach (var node in document.RootElement.EnumerateArray())
        {
            var id = node.GetProperty("id").GetInt32();
            Assert.True(
                node.TryGetProperty("section", out var section) && !string.IsNullOrWhiteSpace(section.GetString()),
                $"Menu.json 的頂層節點 id={id} 未標註 section。");
        }
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
