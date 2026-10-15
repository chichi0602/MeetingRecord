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
        "article",
        "event",
        "mic",
        "logout",
        "checklist",
        "help_outline",
        // AI 用量分析（0.4.80）。⚠️ 必須是 classic Material Icons——App.razor 載的是
        // Material+Icons+Outlined，用 Material Symbols 專有名稱（monitoring、query_stats）
        // 會渲染成破圖，而這個白名單擋不了那種錯。
        "monetization_on",
        // 系統健康監控（0.4.93 名為系統健康度）。classic Material Icons 有這個名稱，實跑確認過會正常渲染。
        "monitor_heart",
        // 系統管理的兩個子群組與監控與診斷各頁（0.4.120，與 NET10-Blazor-Starter 同名）。
        "manage_accounts",
        "analytics",
        "tune",
        "data_usage",
        // 日誌檢視（0.4.121）。
        "description",
        // 系統例外紀錄（0.4.122）。
        "bug_report",
        // AI 管理群組、AI 對話紀錄、Token 用量（1.0.30，與 NET10-Blazor-Starter 同名）。
        // 都是 classic Material Icons（App.razor 載的 Material+Icons+Outlined 有這三個名稱）。
        "psychology",
        "forum",
        "toll",
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
