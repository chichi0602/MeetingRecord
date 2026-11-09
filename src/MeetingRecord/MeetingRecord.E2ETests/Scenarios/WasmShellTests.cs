using System.Text.RegularExpressions;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// WASM 遷移 M1（1.1.1）：WASM 空殼頁 <c>/wasm</c>。遷移期間逐頁切換，已遷移的頁在瀏覽器執行、其餘仍是 Blazor Server，
/// 兩種頁之間整頁載入。這裡驗證兩邊能互相切換、登入狀態與選單與 Server 頁一致。
/// </summary>
public sealed class WasmShellTests(E2EAppFixture app) : E2ETestBase(app)
{
    public static TheoryData<string> Personas => ["管理者", "主責團隊成員", "檢視者", "甲方", "無團隊使用者"];

    [E2ETheory]
    [MemberData(nameof(Personas))]
    [Trait("Scenario", "WASM-01")]
    [Trait("Title", "WASM 空殼頁的選單與 Server 頁側邊欄完全相同")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "依人物")]
    public async Task WasmMenu_ShouldMatchServerSidebar(string persona)
    {
        var user = persona switch
        {
            "管理者" => Seed.Admin,
            "主責團隊成員" => Seed.Owner,
            "檢視者" => Seed.Viewer,
            "甲方" => Seed.Client,
            _ => Seed.NoTeam,
        };
        var page = await LoginAsAsync(user);

        await GotoAsync(page, "/dashboard", "營運概況");
        var serverLinks = page.Locator(".sidebar a[href]:not(.navbar-brand)");
        await Expect(serverLinks.First).ToBeAttachedAsync();
        var expected = await HrefsAsync(serverLinks);

        await page.GotoAsync("/wasm");
        var wasmLinks = page.Locator("nav.wasm-menu a[href]");
        await Expect(wasmLinks.First).ToBeVisibleAsync();
        var actual = await HrefsAsync(wasmLinks);

        Assert.Equal(expected, actual);
        await Expect(page.Locator(".wasm-home-user")).ToHaveTextAsync(user.Name.Length > 0 ? user.Name : user.Account);
    }

    [E2EFact]
    [Trait("Scenario", "WASM-02")]
    [Trait("Title", "WASM 空殼頁與 Server 頁可以互相切換")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "主責團隊成員")]
    public async Task WasmAndServerPages_ShouldNavigateBothWays()
    {
        var page = await LoginAsAsync(Seed.Owner);

        await page.GotoAsync("/wasm");
        await Expect(page.GetByText("WASM 預覽").First).ToBeVisibleAsync();

        // WASM → Server：點選單連結，整頁載入成 Blazor Server 頁。
        await page.Locator("a[href='/meetings']").First.ClickAsync();
        await page.WaitForURLAsync(new Regex("/meetings$"));
        await Expect(page.GetByText("會議標題").First).ToBeVisibleAsync();

        // Server → WASM：在 Server 頁內部導覽（不是直接輸入網址），也要整頁載入成 WASM。
        await page.EvaluateAsync("() => Blazor.navigateTo('/wasm')");
        await page.WaitForURLAsync(new Regex("/wasm$"));
        await Expect(page.GetByText("WASM 預覽").First).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "WASM-03")]
    [Trait("Title", "未登入開 WASM 頁會導到登入頁")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "未登入")]
    public async Task WasmPage_NotLoggedIn_ShouldGoToLogin()
    {
        var page = await NewPageAsync();

        await page.GotoAsync("/wasm");

        await page.WaitForURLAsync(new Regex("/Auths/Login", RegexOptions.IgnoreCase));
    }

    [E2EFact]
    [Trait("Scenario", "WASM-04")]
    [Trait("Title", "須改密碼的帳號開 WASM 頁會導到變更密碼頁")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "須改密碼")]
    public async Task WasmPage_MustChangePassword_ShouldGoToChangePassword()
    {
        var page = await LoginAsAsync(Seed.MustChangePassword);

        await page.GotoAsync("/wasm");

        await page.WaitForURLAsync(new Regex("/ChangePassword", RegexOptions.IgnoreCase));
    }

    private static async Task<List<string>> HrefsAsync(ILocator links)
        => (await links.EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
