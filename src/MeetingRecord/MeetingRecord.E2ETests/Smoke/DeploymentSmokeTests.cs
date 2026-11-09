using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace MeetingRecord.E2ETests.Smoke;

/// <summary>
/// 部署後的上傳冒煙測試（1.0.64）：對**已經部署好的網站**（例如 IIS 正式機）實際上傳一個大檔，確認分塊上傳在那台機器上可用。
/// 由 <c>scripts/Test-IisUpload.ps1</c> 呼叫；沒有設定 <c>MEETINGRECORD_SMOKE_URL</c> 時自動略過。
///
/// <para>
/// ⚠️ 刻意<b>不留任何資料</b>、也<b>不觸發付費 AI</b>：在專案的編輯視窗選附件（選檔當下就會以 HTTP 分塊傳到伺服器暫存），
/// 等「待上傳檔案」出現後，按取消並放棄，暫存檔隨之刪除，專案沒有任何變更。
/// </para>
///
/// <para>不使用端對端測試的種子資料與測試主機：帳號、密碼、網址都由腳本傳入。</para>
/// </summary>
[Trait("Category", "DeploymentSmoke")]
public sealed class DeploymentSmokeTests(ITestOutputHelper output)
{
    [SmokeFact]
    public async Task ChunkedUpload_ShouldWorkOnDeployedSite()
    {
        var settings = SmokeSettings.FromEnvironment();
        var file = Path.Combine(Path.GetTempPath(), $"smoke-upload-{Guid.NewGuid():N}.bin");
        var chunks = new List<(string Url, int Status)>();
        var step = "產生測試檔";
        IPage? page = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        try
        {
            await using (var stream = File.Create(file))
            {
                var buffer = new byte[1024 * 1024];
                for (var i = 0; i < settings.SizeMb; i++)
                {
                    Random.Shared.NextBytes(buffer);
                    await stream.WriteAsync(buffer);
                }
            }

            // 瀏覽器在 finally 才關：失敗時 catch 還要截圖（用 using 的話進 catch 前就已經關掉了）。
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = Environment.GetEnvironmentVariable("E2E_BROWSER_CHANNEL") ?? "msedge",
                Headless = Environment.GetEnvironmentVariable("SMOKE_HEADED") != "1",
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = settings.BaseUrl,
                Locale = "zh-TW",
                IgnoreHTTPSErrors = settings.IgnoreTlsErrors,
                ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
            });
            context.SetDefaultTimeout(60_000);
            page = await context.NewPageAsync();
            page.Response += (_, response) =>
            {
                if (response.Url.Contains("/api/v1/uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    lock (chunks)
                    {
                        chunks.Add((response.Url, response.Status));
                    }
                }
            };

            // 1. 登入（驗證碼是畫面上印出來的 4 碼）
            step = "登入";
            await page.GotoAsync("/Auths/Login");
            await page.Locator("#account").FillAsync(settings.Account);
            await page.Locator("#password").FillAsync(settings.Password);
            await page.Locator("#captcha").FillAsync((await page.Locator(".captcha-display").InnerTextAsync()).Trim());
            await page.GetByRole(AriaRole.Button, new() { Name = "登入系統" }).ClickAsync();
            try
            {
                await page.WaitForURLAsync(url => !url.Contains("/Auths/Login", StringComparison.OrdinalIgnoreCase), new() { Timeout = 30_000 });
            }
            catch (TimeoutException)
            {
                var error = await page.Locator("p.error-message").AllInnerTextsAsync();
                Assert.Fail($"登入失敗：{string.Join(" ", error)}");
            }

            // 2. 專案頁、選專案、開編輯視窗
            step = "開啟專案頁與編輯視窗";
            await page.GotoAsync("/projects");
            var select = page.Locator(".project-view-project-select");
            await Expect(select.Or(page.GetByText("目前沒有可存取的專案")).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            Assert.True(await select.CountAsync() > 0, "這個帳號看不到任何專案，請換一個有專案的帳號，或用 -Project 指定。");
            if (!string.IsNullOrWhiteSpace(settings.Project))
            {
                await select.ClickAsync();
                var options = page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option");
                await Expect(options.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
                var match = options.Filter(new() { HasText = settings.Project });
                if (await match.CountAsync() == 0)
                {
                    var names = string.Join("、", (await options.AllInnerTextsAsync()).Take(10));
                    Assert.Fail($"專案下拉裡找不到「{settings.Project}」（這個帳號看得到的前幾個：{names}）。只會列出「使用中」的專案。");
                }

                await match.First.ClickAsync();
            }

            var edit = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("編輯專案$") });
            await Expect(edit).ToBeEnabledAsync(new() { Timeout = 30_000 });
            await edit.ClickAsync();
            var modal = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "專案附件" });

            // 3. 選檔＝開始分塊上傳；等「待上傳檔案」出現
            step = "上傳附件";
            var fileName = Path.GetFileName(file);
            var timer = Stopwatch.StartNew();
            await modal.Locator("input[type=file]").SetInputFilesAsync(file);
            var pending = modal.Locator(".list-group-item").Filter(new() { HasText = fileName });
            var deadline = TimeSpan.FromSeconds(120 + settings.SizeMb * 2);
            while (await pending.CountAsync() == 0)
            {
                lock (chunks)
                {
                    if (chunks.Any(c => c.Status == 405))
                    {
                        Assert.Fail("分塊上傳被伺服器以 405 擋下：IIS 的 WebDAV 模組攔截了 PUT。請依《正式部署與安全檢查清單》移除 WebDAV 後再試。");
                    }

                    if (chunks.FirstOrDefault(c => c.Status is not 200) is { Url: not null } bad)
                    {
                        Assert.Fail($"分塊上傳失敗：HTTP {bad.Status}（{bad.Url}）。");
                    }
                }

                Assert.True(timer.Elapsed < deadline, $"等了 {timer.Elapsed.TotalSeconds:F0} 秒還沒傳完（已收到 {chunks.Count} 塊回應）。");
                await page.WaitForTimeoutAsync(500);
            }

            timer.Stop();

            // 4. 取消並放棄：不存任何資料
            step = "取消並放棄（不存資料）";
            await modal.Locator(".ant-modal-footer .ant-btn").Filter(new() { HasTextRegex = new Regex(@"^\s*取\s*消\s*$") }).ClickAsync();
            // 選了檔案就算改過表單，會跳「放棄編修」確認框。
            var discard = page.Locator(".ant-modal-confirm .ant-btn").Filter(new() { HasTextRegex = new Regex(@"^\s*放\s*棄\s*$") });
            await discard.First.ClickAsync(new() { Timeout = 15_000 });
            await Expect(modal).ToHaveCountAsync(0, new() { Timeout = 15_000 });

            var seconds = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
            output.WriteLine($"網站：{settings.BaseUrl}");
            output.WriteLine($"檔案：{settings.SizeMb} MB，共 {chunks.Count} 塊，全部 HTTP 200");
            output.WriteLine($"耗時：{seconds:F1} 秒，平均 {settings.SizeMb / seconds:F1} MB/s");
            output.WriteLine("已取消並放棄：專案沒有任何變更，暫存檔已由網站刪除。");
            Assert.True(chunks.Count >= settings.SizeMb / 8, $"分塊回應數 {chunks.Count} 少於預期，請確認上傳確實走分塊（1.0.60 以後）。");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // 在正式機實測時要看得出卡在哪一步：附上步驟名稱與截圖。
            var screenshot = Path.Combine(Path.GetTempPath(), $"smoke-upload-failure-{DateTime.Now:yyyyMMddHHmmss}.png");
            if (page is not null)
            {
                try
                {
                    await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
                }
                catch (PlaywrightException)
                {
                    screenshot = "（截圖失敗）";
                }
            }

            throw new InvalidOperationException($"在「{step}」這一步失敗：{ex.Message}{Environment.NewLine}截圖：{screenshot}", ex);
        }
        finally
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }

            playwright?.Dispose();
            File.Delete(file);
        }
    }
}

/// <summary>只在 <c>MEETINGRECORD_SMOKE_URL</c> 有設定時執行。</summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEETINGRECORD_SMOKE_URL")))
        {
            Skip = "部署冒煙測試：請用 scripts/Test-IisUpload.ps1 執行。";
        }
    }
}

internal sealed record SmokeSettings(string BaseUrl, string Account, string Password, int SizeMb, string? Project, bool IgnoreTlsErrors)
{
    public static SmokeSettings FromEnvironment()
    {
        static string Required(string name)
            => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"缺少環境變數 {name}（請用 scripts/Test-IisUpload.ps1 執行）。");

        return new SmokeSettings(
            Required("MEETINGRECORD_SMOKE_URL").TrimEnd('/'),
            Required("MEETINGRECORD_SMOKE_ACCOUNT"),
            Required("MEETINGRECORD_SMOKE_PASSWORD"),
            int.TryParse(Environment.GetEnvironmentVariable("MEETINGRECORD_SMOKE_SIZE_MB"), out var size) && size > 0 ? size : 200,
            Environment.GetEnvironmentVariable("MEETINGRECORD_SMOKE_PROJECT"),
            Environment.GetEnvironmentVariable("MEETINGRECORD_SMOKE_IGNORE_TLS") == "1");
    }
}
