using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// 側邊選單依人物顯示（權限矩陣見 docs/security/權限與資料可見範圍總覽.md）。
/// 側邊欄預設收合：第一層是 <c>a[aria-label]</c>、群組是 <c>button[aria-label]</c>，第二、三層是隱藏的 <c>menuitem</c>（要 IncludeHidden 才找得到）。
/// </summary>
public sealed class MenuTests(E2EAppFixture app) : E2ETestBase(app)
{
    public static TheoryData<string> Personas => ["管理者", "主責團隊成員", "檢視者", "甲方", "無團隊使用者"];

    [E2ETheory]
    [MemberData(nameof(Personas))]
    [Trait("Scenario", "MNU-01")]
    [Trait("Title", "側邊選單依人物顯示")]
    [Trait("Page", "側邊選單")]
    [Trait("Persona", "依人物")]
    public async Task SidebarMenu_ShouldMatchPersonaPermissions(string persona)
    {
        var (user, visible, hidden) = persona switch
        {
            "管理者" => (Seed.Admin,
                new[] { "儀表板", "會議紀錄", "專案項目", "待辦事項", "使用說明", "系統管理", "資料定義", "使用者管理", "稽核紀錄", "系統健康監控", "Token 用量" },
                Array.Empty<string>()),
            "主責團隊成員" => (Seed.Owner,
                new[] { "儀表板", "會議紀錄", "專案項目", "待辦事項", "使用說明" },
                new[] { "系統管理", "資料定義", "使用者管理", "稽核紀錄" }),
            "檢視者" => (Seed.Viewer,
                new[] { "儀表板", "會議紀錄", "專案項目", "待辦事項", "使用說明" },
                new[] { "系統管理", "資料定義", "使用者管理" }),
            "甲方" => (Seed.Client,
                new[] { "儀表板", "專案項目", "待辦事項", "使用說明" },
                new[] { "會議紀錄", "系統管理", "資料定義" }),
            _ => (Seed.NoTeam,
                new[] { "儀表板", "會議紀錄", "專案項目", "待辦事項", "使用說明" },
                new[] { "系統管理", "資料定義", "使用者管理" }),
        };

        var page = await LoginAsAsync(user);
        await GotoAsync(page, "/dashboard", "營運概況");

        foreach (var name in visible)
        {
            Assert.True(await MenuEntry(page, name).CountAsync() > 0, $"{persona} 應該看得到選單「{name}」");
        }

        foreach (var name in hidden)
        {
            Assert.True(await MenuEntry(page, name).CountAsync() == 0, $"{persona} 不應該看到選單「{name}」");
        }
    }

    private static ILocator MenuEntry(IPage page, string name)
        => page.Locator($"a[aria-label=\"{name}\"], button[aria-label=\"{name}\"]")
            // 第二、三層的可及名稱會帶圖示字（例如 manage_accounts 使用者管理），改比對文字那個 span。
            .Or(page.Locator($"a[role=menuitem] > span:text-is(\"{name}\")"));
}
