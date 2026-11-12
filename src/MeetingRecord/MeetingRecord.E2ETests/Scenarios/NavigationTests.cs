using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MeetingRecord.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>WASM 遷移 M6（1.1.45）：首頁與找不到頁也在瀏覽器執行，應用程式不再用到 Blazor Server 連線。</summary>
public sealed class NavigationTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "NAV-01")]
    [Trait("Title", "首頁導到會議紀錄、亂打網址顯示找不到頁（不會一直重新載入），全程沒有 Blazor Server 連線")]
    [Trait("Page", "首頁與找不到頁")]
    [Trait("Persona", "主責團隊成員")]
    public async Task HomeAndUnknownUrl_ShouldStayInWasm()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var sockets = new ConcurrentQueue<string>();
        page.WebSocket += (_, ws) => sockets.Enqueue(ws.Url);
        var documentLoads = 0;
        page.Request += (_, request) =>
        {
            if (request.ResourceType == "document")
            {
                Interlocked.Increment(ref documentLoads);
            }
        };

        // 首頁：登入狀態由 WasmLayout 處理，通過後換到會議紀錄。
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/meetings$"));
        await Expect(page.GetByText("會議標題").First).ToBeVisibleAsync();

        // 亂打網址：伺服器回 404 轉到 /not-found（WASM 頁），只載入一次，不會在伺服器與瀏覽器之間來回重載。
        var before = Volatile.Read(ref documentLoads);
        await page.GotoAsync($"/no-such-page-{Guid.NewGuid():N}");
        await Expect(page.GetByText("找不到這個頁面")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1500);
        Assert.Equal(before + 1, Volatile.Read(ref documentLoads));

        Assert.DoesNotContain(sockets, url => url.Contains("/_blazor", StringComparison.OrdinalIgnoreCase));
    }

    [E2EFact]
    [Trait("Scenario", "ERR-01")]
    [Trait("Title", "WASM 頁的瀏覽器錯誤會回報到伺服器，記進系統例外紀錄（來源「瀏覽器」）")]
    [Trait("Page", "系統例外紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task BrowserError_OnWasmPage_ShouldReachTheExceptionLog()
    {
        // 1.1.47：以前經 Blazor Server 的連線回報，WASM 頁的錯誤送不出去；改走 POST api/v1/client-errors。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var message = $"e2e-browser-error-{Guid.NewGuid():N}";

        await page.EvaluateAsync($"() => setTimeout(() => {{ throw new Error('{message}'); }}, 0)");

        await using var db = App.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            // 瀏覽器的 error 事件訊息會帶「Uncaught Error: 」前綴，所以比對包含。
            var row = await db.ExceptionLog.AsNoTracking().SingleOrDefaultAsync(x => x.Message.Contains(message));
            if (row is not null)
            {
                Assert.Equal("瀏覽器", row.Source);
                Assert.Equal("/meetings", row.Page);
                Assert.Equal(Seed.Owner.Account, row.Account);
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "瀏覽器錯誤沒有記進系統例外紀錄");
            await Task.Delay(300);
        }
    }

    [E2EFact]
    [Trait("Scenario", "ERR-02")]
    [Trait("Title", "頁面上的 .NET 例外：畫面顯示錯誤提示，並記進系統例外紀錄（來源「瀏覽器」、種類 dotnet）")]
    [Trait("Page", "系統例外紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task DotNetException_OnWasmPage_ShouldShowTheBannerAndReachTheExceptionLog()
    {
        // 1.1.54：.NET 例外不會觸發瀏覽器的 error 事件，由 DotNetErrorForwarder 送出。
        // 讓儀表板 API 回傳格式正確、但卡片清單是 null 的資料，頁面算繪時丟 NullReferenceException，由 WasmLayout 的 ErrorBoundary 接住。
        var page = await LoginAsAsync(Seed.Owner);
        await page.RouteAsync("**/api/v1/dashboard", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"success":true,"statusCode":200,"message":"ok","data":{"cards":null}}""",
        }));

        await page.GotoAsync("/dashboard");
        await Expect(page.GetByText("這個頁面發生未預期的錯誤")).ToBeVisibleAsync();

        await using var db = App.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var row = await db.ExceptionLog.AsNoTracking()
                .Where(x => x.Page == "/dashboard" && x.Message.Contains("System.NullReferenceException"))
                .SingleOrDefaultAsync();
            if (row is not null)
            {
                Assert.Equal("瀏覽器", row.Source);
                Assert.Equal(Seed.Owner.Account, row.Account);
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, ".NET 例外沒有記進系統例外紀錄");
            await Task.Delay(300);
        }
    }
}
