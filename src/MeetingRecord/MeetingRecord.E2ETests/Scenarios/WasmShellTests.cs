using System.Text.RegularExpressions;
using MeetingRecord.AccessDatas.Models;
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

    [E2EFact]
    [Trait("Scenario", "WASM-05")]
    [Trait("Title", "WASM 頁的未讀通知數經推播即時更新（不必重新整理）")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "主責團隊成員＋協作團隊成員（兩個瀏覽器）")]
    public async Task WasmUnreadCount_ShouldUpdateByPush()
    {
        var title = Unique("E2E 推播待辦");
        await using (var db = App.CreateDbContext())
        {
            db.Todo.Add(new Todo { Title = title, ProjectId = Seed.SharedProjectId, Status = "待辦", Priority = "中", Owner = Seed.Owner.Name, CreatedByUserId = Seed.Owner.Id });
            await db.SaveChangesAsync();
        }

        // 負責人開著 WASM 頁（收推播的一方）。
        var owner = await LoginAsAsync(Seed.Owner);
        await owner.GotoAsync("/wasm");
        var unread = owner.Locator(".wasm-unread");
        await Expect(unread).ToBeVisibleAsync();
        var before = int.Parse(await unread.GetAttributeAsync("data-count") ?? "0");

        // 協作成員在 Server 待辦頁改狀態（與 NTF-01 同樣的步驟），會寫通知給負責人。
        var collaborator = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(collaborator, "/todos", "待辦事項");
        await collaborator.GetByPlaceholder("搜尋關鍵字").FillAsync(title);
        await ToolbarButton(collaborator, "搜尋").ClickAsync();
        await RowAction(Row(collaborator, title), "修改").ClickAsync();
        await PickAsync(collaborator, SelectByLabel(collaborator, "狀態"), "進行中");
        await ClickModalOkAsync(collaborator);
        await Expect(Notice(collaborator, "修改成功")).ToBeVisibleAsync();

        // 負責人那邊不重新整理，數字自己變多。
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (int.Parse(await unread.GetAttributeAsync("data-count") ?? "0") <= before)
        {
            Assert.True(DateTime.UtcNow < deadline, $"等了 20 秒未讀數還是 {before}，推播沒有到");
            await owner.WaitForTimeoutAsync(200);
        }
    }

    private static async Task<List<string>> HrefsAsync(ILocator links)
        => (await links.EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
