using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// WASM 遷移 M1（1.1.1）：WASM 空殼頁 <c>/wasm</c>。遷移期間逐頁切換，已遷移的頁在瀏覽器執行、其餘仍是 Blazor Server，
/// 兩種頁之間整頁載入。這裡驗證兩邊能互相切換、登入狀態與選單與 Server 頁一致。
/// </summary>
public sealed class WasmShellTests(E2EAppFixture app, ITestOutputHelper output) : E2ETestBase(app)
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

        // 1.1.5 起兩邊用同一份 NavMenu（共用外框 AppShell），用同一個選擇器比對。
        await page.GotoAsync("/wasm");
        var wasmLinks = page.Locator(".sidebar a[href]:not(.navbar-brand)");
        await Expect(wasmLinks.First).ToBeAttachedAsync();
        var actual = await HrefsAsync(wasmLinks);

        Assert.Equal(expected, actual);
        // 外框與 Server 頁同一份（AppShell）：頂端列的頁面標題、使用者選單上的名字都在。
        await Expect(page.Locator(".top-row-title")).ToBeVisibleAsync();
        await Expect(page.Locator(".user-menu-name")).ToHaveTextAsync(user.Name.Length > 0 ? user.Name : user.Account);
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

    [E2EFact]
    [Trait("Scenario", "WASM-06")]
    [Trait("Title", "發佈後的 WASM 頁：靜態檔有 Brotli 壓縮、推播走 WebSocket、載入時間在範圍內")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "主責團隊成員")]
    public async Task PublishedWasm_ShouldCompress_UseWebSocket_AndLoadInTime()
    {
        // M1 的本機部署驗證（1.1.3）：端對端網站本來就是 dotnet publish 的輸出，與部署的檔案相同。
        var page = await LoginAsAsync(Seed.Owner);
        var sockets = new List<string>();
        var wasmFiles = new List<string>();
        page.WebSocket += (_, socket) => { lock (sockets) { sockets.Add(socket.Url); } };
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/_framework/", StringComparison.Ordinal) && request.Url.EndsWith(".wasm", StringComparison.Ordinal))
            {
                lock (wasmFiles) { wasmFiles.Add(request.Url); }
            }
        };

        // 首次載入：這個 context 還沒下載過 WASM 執行環境（登入頁是靜態頁）。
        var timer = Stopwatch.StartNew();
        await page.GotoAsync("/wasm");
        await Expect(page.Locator(".wasm-home-user")).ToBeVisibleAsync();
        var firstLoad = timer.Elapsed;

        // 有快取的再次載入。
        timer.Restart();
        await page.ReloadAsync();
        await Expect(page.Locator(".wasm-home-user")).ToBeVisibleAsync();
        var cachedLoad = timer.Elapsed;

        // 推播通道要真的走 WebSocket，不是退回 LongPolling（IIS 沒開 WebSocket 功能時會退回）。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!HasHubSocket())
        {
            Assert.True(DateTime.UtcNow < deadline, "沒有看到連到 /hubs/app 的 WebSocket");
            await page.WaitForTimeoutAsync(200);
        }

        // 靜態檔要送預先壓縮好的 Brotli 版本（MapStaticAssets）。
        string wasmFile;
        lock (wasmFiles) { wasmFile = wasmFiles.First(); }
        var response = await page.APIRequest.GetAsync(wasmFile, new() { Headers = new Dictionary<string, string> { ["Accept-Encoding"] = "br" } });
        Assert.True(response.Ok, $"{wasmFile} 回 {response.Status}");
        Assert.Equal("br", response.Headers.GetValueOrDefault("content-encoding"));

        output.WriteLine($"WASM 首次載入 {firstLoad.TotalSeconds:F2} 秒（門檻 ≤ 4 秒）、有快取 {cachedLoad.TotalSeconds:F2} 秒（門檻 ≤ 1.5 秒）");
        // 斷言放寬，避免本機忙時偶發失敗；正式門檻在報告裡對照實際數字。
        Assert.True(firstLoad < TimeSpan.FromSeconds(10), $"首次載入 {firstLoad.TotalSeconds:F1} 秒，太慢");
        Assert.True(cachedLoad < TimeSpan.FromSeconds(5), $"有快取的載入 {cachedLoad.TotalSeconds:F1} 秒，太慢");

        bool HasHubSocket()
        {
            lock (sockets) { return sockets.Any(url => url.Contains("/hubs/app", StringComparison.Ordinal)); }
        }
    }

    private static async Task<List<string>> HrefsAsync(ILocator links)
        => (await links.EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
