using System.Globalization;
using Xunit.Abstractions;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// L5 非功能（WASM 遷移 M6，1.1.50；門檻見 09 號文件 §8 L5）。每則把量到的數字以
/// <c>L5METRIC|項目|門檻|實測|判定</c> 一行寫進測試輸出，<c>New-TestReport.ps1</c> 據此產生報告第五節。
///
/// <para>
/// 載入時間在頁面裡量：導覽開始（<c>performance.now()</c> 的原點）到登入者名稱出現在畫面上的那一刻，
/// 由事先注入的 <c>MutationObserver</c> 記下——不含 Playwright 自己輪詢與等待的開銷（以前 WASM-06 用 Stopwatch 包整段，會多 0.3～0.5 秒）。
/// </para>
/// </summary>
public sealed class PerformanceTests(E2EAppFixture app, ITestOutputHelper output) : E2ETestBase(app)
{
    private const string VisibleMarkScript = """
        (() => {
          const mark = () => {
            if (!window.__l5Visible && document.querySelector('.wasm-home-user')) {
              window.__l5Visible = performance.now();
            }
          };
          new MutationObserver(mark).observe(document, { childList: true, subtree: true });
        })();
        """;

    [E2EFact]
    [Trait("Scenario", "L5-01")]
    [Trait("Title", "WASM 首次載入（沒有快取）≤ 4 秒；有快取的再次載入記錄實測（門檻 1.5 秒，已知差異，超過 3 秒才失敗）")]
    [Trait("Page", "WASM 預覽")]
    [Trait("Persona", "主責團隊成員")]
    public async Task LoadTimes_ShouldMeetTheThresholds()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await page.AddInitScriptAsync(VisibleMarkScript);

        // 首次載入：這個 context 還沒下載過 WASM 執行環境（登入頁是靜態頁）。
        await page.GotoAsync("/wasm");
        var first = await VisibleAtAsync(page);

        // 有快取的再次載入：量 3 次取中位數。
        var cached = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            await page.WaitForTimeoutAsync(500);
            await page.ReloadAsync();
            cached.Add(await VisibleAtAsync(page));
        }

        cached.Sort();
        var cachedMedian = cached[1];
        Report("首次載入（沒有快取）", "≤ 4 秒", $"{first / 1000:F2} 秒", first <= 4000);
        Report("有快取的再次載入（3 次中位數）", "≤ 1.5 秒", $"{cachedMedian / 1000:F2} 秒（{string.Join("、", cached.Select(x => (x / 1000).ToString("F2", CultureInfo.InvariantCulture)))}）", cachedMedian <= 1500);

        Assert.True(first <= 4000, $"首次載入 {first / 1000:F2} 秒，超過 4 秒");
        // 有快取的再次載入：1.1.50 實測約 1.7 秒（主要是 .NET WASM 執行環境啟動），超過 1.5 秒門檻；
        // 使用者 2026-11-11 決定列入已知差異（報告照實標「未達標」），測試只在超過 3 秒時失敗，用來防止退步。
        Assert.True(cachedMedian <= 3000, $"有快取的再次載入 {cachedMedian / 1000:F2} 秒，超過 3 秒（已知差異的上限）");
    }

    [E2EFact]
    [Trait("Scenario", "L5-02")]
    [Trait("Title", "一般查詢 API 的 p95 ≤ 300 ms")]
    [Trait("Page", "Web API")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ApiP95_ShouldMeetTheThreshold()
    {
        // 頁面進場會打的一般查詢（登入狀態、選單、儀表板、背景工作、通知），在瀏覽器裡實際呼叫，暖身一輪後量 10 輪。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var p95 = await page.EvaluateAsync<double>("""
            async () => {
              const urls = ['/api/v1/session', '/api/v1/session/menu', '/api/v1/dashboard', '/api/v1/jobs',
                            '/api/v1/notifications', '/api/v1/notifications/unread-count'];
              const call = async (u) => { const s = performance.now(); const r = await fetch(u, { credentials: 'include' }); await r.text(); if (!r.ok) throw new Error(u + ' ' + r.status); return performance.now() - s; };
              for (const u of urls) await call(u);
              const samples = [];
              for (let i = 0; i < 10; i++) for (const u of urls) samples.push(await call(u));
              samples.sort((a, b) => a - b);
              return samples[Math.ceil(samples.length * 0.95) - 1];
            }
            """);

        Report("一般查詢 API p95（6 支 × 10 輪）", "≤ 300 ms", $"{p95:F0} ms", p95 <= 300);
        Assert.True(p95 <= 300, $"API p95 {p95:F0} ms，超過 300 ms");
    }

    [E2EFact]
    [Trait("Scenario", "L5-03")]
    [Trait("Title", "伺服器記憶體（只記錄）")]
    [Trait("Page", "Web API")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ServerMemory_ShouldBeRecorded()
    {
        // 09 號的門檻是「與 Server 版相比明顯較低、使用者增加時持平」：Server 版已不存在，這裡只記錄單一使用者進過頁面後的工作集。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var bytes = App.ServerWorkingSetBytes;
        Report("伺服器工作集（單一使用者進過會議頁）", "只記錄", bytes is { } b ? $"{b / 1024d / 1024:F0} MB" : "（外部網站，量不到）", null);
    }

    /// <summary>等登入者名稱出現，回傳從導覽開始到出現的毫秒數（頁面裡量）。</summary>
    private static async Task<double> VisibleAtAsync(IPage page)
    {
        await Expect(page.Locator(".wasm-home-user")).ToBeVisibleAsync();
        return await page.EvaluateAsync<double>("() => window.__l5Visible ?? performance.now()");
    }

    private void Report(string item, string threshold, string actual, bool? passed)
        => output.WriteLine($"L5METRIC|{item}|{threshold}|{actual}|{(passed is null ? "—" : passed.Value ? "✅" : "❌")}");
}
