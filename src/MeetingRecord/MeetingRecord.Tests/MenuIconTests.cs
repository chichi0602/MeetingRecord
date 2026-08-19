using System.Text.Json;

namespace MeetingRecord.Tests;

/// <summary>
/// 驗證 Datas/Menu.json 內所有節點的 icon 名稱皆為「有效的 classic Material Icons」名稱。
///
/// 背景：App 載入的是 classic Material Icons 字型；若選單用到 Material Symbols 專有名稱
/// （例如 "database"），會在側邊欄渲染為破圖。本測試把允許清單維護在程式內，
/// 新增選單若用到清單外的 icon 名，測試會失敗，強制開發者確認該名稱是否有效並加入清單。
/// </summary>
public sealed class MenuIconTests
{
    /// <summary>
    /// 目前 Menu.json 使用且確認有效的 classic Material Icons 名稱允許清單。
    /// 新增選單 icon 時，請先確認其為有效的 classic Material Icons 名稱，再加入此清單。
    /// </summary>
    private static readonly HashSet<string> AllowedIcons = new(StringComparer.Ordinal)
    {
        "space_dashboard",
        "work",
        "workspaces",
        "admin_panel_settings",
        "group",
        "shield_person",
        "storage",
        "category",
        "groups",
        "logout",
    };

    [Fact]
    public void MenuJson_AllIcons_ShouldBeNonEmptyAndAllowed()
    {
        var menuJsonPath = FindMenuJsonPath();
        using var document = JsonDocument.Parse(File.ReadAllText(menuJsonPath));

        var icons = new List<string>();
        foreach (var node in document.RootElement.EnumerateArray())
        {
            CollectIcons(node, icons);
        }

        Assert.NotEmpty(icons);

        foreach (var icon in icons)
        {
            Assert.False(string.IsNullOrWhiteSpace(icon), "Menu.json 內有節點缺少 icon 名稱。");
            Assert.True(
                AllowedIcons.Contains(icon),
                $"Menu.json 使用了不在允許清單中的 icon 名稱：'{icon}'。" +
                "請確認其為有效的 classic Material Icons 名稱後，加入 MenuIconTests.AllowedIcons。");
        }
    }

    private static void CollectIcons(JsonElement node, List<string> icons)
    {
        if (node.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String)
        {
            icons.Add(iconElement.GetString() ?? string.Empty);
        }

        if (node.TryGetProperty("subMenu", out var subMenu) && subMenu.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in subMenu.EnumerateArray())
            {
                CollectIcons(child, icons);
            }
        }
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
