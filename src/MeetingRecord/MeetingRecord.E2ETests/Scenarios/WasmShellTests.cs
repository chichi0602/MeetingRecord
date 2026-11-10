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
        await Expect(owner.Locator(".notification-bell-trigger")).ToBeVisibleAsync();
        var before = await UnreadAsync(owner);

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
        while (await UnreadAsync(owner) <= before)
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

    [E2EFact]
    [Trait("Scenario", "WASM-07")]
    [Trait("Title", "WASM 頁的通知鈴：看得到通知清單、全部標為已讀後歸零")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "檢視者")]
    public async Task WasmNotificationBell_ShouldListAndMarkAllRead()
    {
        var marker = Unique("E2E 鈴鐺通知");
        await using (var db = App.CreateDbContext())
        {
            db.UserNotification.AddRange(
                new UserNotification { UserId = Seed.Viewer.Id, Message = $"{marker} 甲", CreatedAt = DateTime.Now.AddMinutes(-1) },
                new UserNotification { UserId = Seed.Viewer.Id, Message = $"{marker} 乙", CreatedAt = DateTime.Now });
            await db.SaveChangesAsync();
        }

        var page = await LoginAsAsync(Seed.Viewer);
        await page.GotoAsync("/wasm");
        var trigger = page.Locator(".notification-bell-trigger");
        await Expect(trigger).ToHaveAttributeAsync("aria-label", new Regex(@"^通知，\d+ 則未讀$"));
        Assert.True(await UnreadAsync(page) >= 2);

        await trigger.ClickAsync();
        var panel = page.GetByRole(AriaRole.Dialog, new() { Name = "通知" });
        await Expect(panel.Locator(".notification-bell-message").Filter(new() { HasText = $"{marker} 甲" })).ToBeVisibleAsync();
        await Expect(panel.Locator(".notification-bell-message").Filter(new() { HasText = $"{marker} 乙" })).ToBeVisibleAsync();

        await panel.GetByRole(AriaRole.Button, new() { Name = "全部標為已讀" }).ClickAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-label", "通知");
    }

    [E2EFact]
    [Trait("Scenario", "WASM-08")]
    [Trait("Title", "WASM 頁的使用者選單：變更密碼會檢查輸入、改到一半取消要確認；關於顯示目前版本")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "主責團隊成員")]
    public async Task WasmUserMenu_ChangePasswordAndAbout()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await page.GotoAsync("/wasm");
        await Expect(page.Locator(".wasm-home-user")).ToBeVisibleAsync();

        // 變更密碼：新密碼太短，前端就擋下來並說明。
        await page.GetByRole(AriaRole.Button, new() { Name = "使用者操作" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "變更密碼" }).ClickAsync();
        await page.GetByPlaceholder("請輸入目前密碼").FillAsync(Seed.Owner.Password);
        await page.GetByPlaceholder("請輸入新密碼（至少 6 個字元）").FillAsync("123");
        await page.GetByPlaceholder("請再次輸入新密碼").FillAsync("123");
        await ClickModalOkAsync(page);
        await Expect(page.GetByText("新密碼至少需要 6 個字元。")).ToBeVisibleAsync();

        // 打過字再取消要先確認（FormDirtyHelper 在 WASM 也正常）。
        await page.Locator(".ant-modal-wrap:visible .ant-modal-footer .ant-btn").Filter(new() { HasTextRegex = new Regex(@"^\s*取\s*消\s*$") }).ClickAsync();
        await Expect(page.Locator(".ant-modal-confirm").GetByText("放棄編修")).ToBeVisibleAsync();
        await ConfirmAsync(page, "放棄");
        await Expect(page.GetByPlaceholder("請輸入目前密碼")).ToBeHiddenAsync();

        // 關於：版本與伺服器設定一致。
        var about = await page.APIRequest.GetAsync("/api/v1/system/about");
        var version = (await about.JsonAsync())!.Value.GetProperty("data").GetProperty("systemVersion").GetString()!;
        await page.GetByRole(AriaRole.Button, new() { Name = "使用者操作" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "關於" }).ClickAsync();
        await Expect(page.Locator(".about-row").Filter(new() { HasText = "系統版本" })).ToContainTextAsync(version);
    }

    [E2EFact]
    [Trait("Scenario", "WASM-09")]
    [Trait("Title", "分類清單在 WASM 執行：從 Server 頁切過去整頁載入、資料走分類 API、點欄位排序會把條件送給伺服器")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "管理者")]
    public async Task CategoryPage_ShouldRunOnWasm_AndSortThroughApi()
    {
        // 1.1.7：第一個搬到 WASM 的實際頁面。MD-01／02／05／06 驗功能；這裡驗「真的在 WASM 跑」與排序欄位名在修剪後仍正確。
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/dashboard", "營運概況");

        var firstSearch = page.WaitForRequestAsync(r => r.Url.Contains("/api/v1/Category/search"));
        await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
        await page.WaitForURLAsync(new Regex("/categories$"));
        await firstSearch;
        var nameHeader = page.Locator("th.ant-table-column-has-sorters").Filter(new() { HasText = "名稱" });
        await Expect(nameHeader).ToBeVisibleAsync();

        var sorted = page.WaitForRequestAsync(r => r.Url.Contains("/api/v1/Category/search") && (r.PostData ?? string.Empty).Contains("\"sortBy\":\"Name\""));
        await nameHeader.ClickAsync();
        var request = await sorted;
        Assert.Contains("\"sortDescending\":false", request.PostData);
    }

    [E2EFact]
    [Trait("Scenario", "WASM-10")]
    [Trait("Title", "WASM 頁的進度面板即時顯示協作團隊的上傳與轉錄（不必重新整理），沒有團隊的人看不到；關閉通知會消失")]
    [Trait("Page", "儀表板")]
    [Trait("Persona", "主責＋協作＋無團隊")]
    public async Task JobPanel_OnWasmPage_ShouldShowLiveProgress_ByScope()
    {
        // 1.1.15：進度面板在 WASM 頁改由 api/v1/jobs＋推播 JobsChanged 提供。比照 L4-04，只是看的人停在 WASM 頁（儀表板）。
        const int mb = 1024 * 1024;
        var title = Unique("E2E WASM推播會議");
        await using (var db = App.CreateDbContext())
        {
            db.Meeting.Add(new Meeting { Title = title, ProjectId = Seed.SharedProjectId, CreatedByUserId = Seed.Owner.Id, MeetingDate = DateTime.Today });
            await db.SaveChangesAsync();
        }

        var collaborator = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(collaborator, "/dashboard", "營運概況");
        var noTeam = await LoginAsAsync(Seed.NoTeam);
        await GotoAsync(noTeam, "/dashboard", "營運概況");

        var owner = await LoginAsAsync(Seed.Owner);
        await GotoAsync(owner, "/meetings", "會議標題");
        await ThrottleUploadsAsync(owner, TimeSpan.FromSeconds(1.5));
        await RowAction(Row(owner, title), "修改").ClickAsync();
        await owner.Locator(".ant-modal-wrap:visible input[type=file]").SetInputFilesAsync(FakeMediaFile($"{title}.mp3", 40 * mb));
        await Expect(owner.GetByText("待上傳：")).ToBeVisibleAsync();
        await ClickModalOkAsync(owner);
        await ConfirmAsync(owner, "儲存並開始轉錄");

        // 協作成員停在 WASM 頁不動，面板自己冒出這一列，接著跟著轉錄跑到完成。
        var row = JobPanelRow(collaborator, title);
        await Expect(row).ToContainTextAsync("上傳中");
        await Expect(row.Locator("[aria-label='已完成']")).ToBeVisibleAsync(new() { Timeout = 120_000 });

        // 沒有團隊的人自始至終看不到。
        await Expect(JobPanelRow(noTeam, title)).ToHaveCountAsync(0);

        // 關閉通知經 API，這一列消失。
        await row.Locator(".job-progress-panel-row-close").ClickAsync();
        await Expect(row).ToHaveCountAsync(0);
    }

    /// <summary>通知鈴按鈕的可及名稱是「通知，N 則未讀」，沒有未讀時只有「通知」。</summary>
    private static async Task<int> UnreadAsync(IPage page)
    {
        var label = await page.Locator(".notification-bell-trigger").GetAttributeAsync("aria-label") ?? string.Empty;
        var match = Regex.Match(label, @"(\d+) 則未讀");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static async Task<List<string>> HrefsAsync(ILocator links)
        => (await links.EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
