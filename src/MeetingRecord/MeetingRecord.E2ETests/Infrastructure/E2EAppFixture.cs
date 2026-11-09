using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.E2ETests.Infrastructure;

/// <summary>
/// 整個端對端測試共用一台網站與一個瀏覽器（啟動一次約十幾秒，每則測試各開一個乾淨的瀏覽器 context）。
///
/// <para>
/// 兩種模式：
/// 1. 預設：以 <c>dotnet MeetingRecord.Web.dll</c> 從 publish 輸出（環境變數 <c>MEETINGRECORD_E2E_APP</c>）啟動一台，
///    所有資料目錄都指到臨時資料夾、環境為 <c>E2E</c>（假 AI）、關掉匯率排程，結束時整個刪掉。
/// 2. <c>E2E_BASE_URL</c>＋<c>E2E_DB_PATH</c>：不自己啟動，直接對已經在跑的網站測（之後對 WASM 版就是這樣用）。
/// </para>
/// </summary>
public sealed class E2EAppFixture : IAsyncLifetime
{
    public const string AdminPassword = "E2e-Admin-Pass-2026";

    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordE2E", Guid.NewGuid().ToString("N"));
    private Process? process;
    private IPlaywright? playwright;

    public string BaseUrl { get; private set; } = string.Empty;

    public string DatabasePath { get; private set; } = string.Empty;

    public IBrowser Browser { get; private set; } = null!;

    public E2ESeedData Seed { get; private set; } = null!;

    /// <summary>測試產生的檔案（下載、上傳用的假音檔）放這裡。</summary>
    public string WorkPath => Path.Combine(rootPath, "work");

    /// <summary>
    /// 網站的專案附件暫存資料夾（1.0.65）。自己啟動網站時把它的 TMP／TEMP 指到 <c>rootPath/tmp</c>，才看得到也不會弄髒本機的 %TEMP%；
    /// 對外部網站測（<c>E2E_BASE_URL</c>）時不知道在哪，為 null。
    /// </summary>
    public string? PendingUploadPath { get; private set; }

    /// <summary>網站存會議影音檔的資料夾（1.0.66，L4 比對落地檔用）；對外部網站測時為 null。</summary>
    public string? MeetingMediaPath { get; private set; }

    public async Task InitializeAsync()
    {
        if (!E2ESwitch.Enabled)
        {
            return;
        }

        Directory.CreateDirectory(WorkPath);
        var externalUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            BaseUrl = externalUrl.TrimEnd('/');
            DatabasePath = Environment.GetEnvironmentVariable("E2E_DB_PATH")
                ?? throw new InvalidOperationException("指定 E2E_BASE_URL 時也要指定 E2E_DB_PATH（資料庫資料夾），種子資料要寫進去。");
        }
        else
        {
            await StartAppAsync();
        }

        Seed = await E2ESeeder.SeedAsync(CreateDbContext);

        // Blazor Server 關掉預先算繪，畫面要等連線建立；斷言的預設等待從 5 秒放寬到 20 秒，與操作一致。
        SetDefaultExpectTimeout(20_000);
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            // 用本機已安裝的 Edge，不必下載 Playwright 自帶的瀏覽器。
            Channel = Environment.GetEnvironmentVariable("E2E_BROWSER_CHANNEL") ?? "msedge",
            Headless = Environment.GetEnvironmentVariable("E2E_HEADED") != "1",
        });
    }

    public BackendDBContext CreateDbContext()
        => new(new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(MagicObjectHelper.GetSQLiteConnectionString(DatabasePath))
            .Options);

    /// <summary>每則測試一個乾淨的瀏覽器 context（不共用 Cookie），下載一律接受。</summary>
    public async Task<IBrowserContext> NewContextAsync()
        => await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            AcceptDownloads = true,
            Locale = "zh-TW",
            ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
        });

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        playwright?.Dispose();

        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process?.Dispose();

        try
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite 或日誌檔可能還沒放開，暫存資料夾留著不影響結果。
        }
    }

    private async Task StartAppAsync()
    {
        var appPath = Environment.GetEnvironmentVariable("MEETINGRECORD_E2E_APP");
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(Path.Combine(appPath, "MeetingRecord.Web.dll")))
        {
            throw new InvalidOperationException(
                "找不到要測試的網站。請先 publish MeetingRecord.Web，並把輸出資料夾設到環境變數 MEETINGRECORD_E2E_APP（scripts/Run-E2E.ps1 會自動做）。");
        }

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        DatabasePath = Path.Combine(rootPath, "DB");

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = appPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var tempPath = Path.Combine(rootPath, "tmp");
        Directory.CreateDirectory(tempPath);
        info.Environment["TMP"] = tempPath;
        info.Environment["TEMP"] = tempPath;
        PendingUploadPath = Path.Combine(tempPath, "MeetingRecord", "pending-uploads");
        MeetingMediaPath = Path.Combine(rootPath, "MeetingMediaPath");
        info.ArgumentList.Add(Path.Combine(appPath, "MeetingRecord.Web.dll"));
        foreach (var argument in BuildArguments(port))
        {
            info.ArgumentList.Add(argument);
        }

        var logPath = Path.Combine(rootPath, "app-console.log");
        var log = new StreamWriter(logPath) { AutoFlush = true };
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"網站啟動失敗（結束代碼 {process.ExitCode}），主控台輸出見 {logPath}");
            }

            try
            {
                using var response = await http.GetAsync($"{BaseUrl}/health/live");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // 還沒開始監聽。
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"網站 2 分鐘內沒有回應 /health/live，主控台輸出見 {logPath}");
            }

            await Task.Delay(500);
        }
    }

    private IEnumerable<string> BuildArguments(int port)
    {
        var settings = new Dictionary<string, string>
        {
            ["environment"] = "E2E",
            ["urls"] = BaseUrl,
            ["NLog:BasePath"] = Path.Combine(rootPath, "Logs"),
            ["ExchangeRateSettings:Enabled"] = "false",
            ["BootstrapSettings:SupportPassword"] = AdminPassword,
            ["LlmSettings:DefaultProvider"] = "Fake",
            ["LlmSettings:TranscriptionProvider"] = "Fake",
            ["LlmSettings:Providers:Fake:ApiKey"] = "e2e-fake-key",
            ["LlmSettings:Providers:Fake:Endpoint"] = "https://fake.invalid/",
            ["LlmSettings:Providers:Fake:Model"] = "fake",
            ["LlmSettings:Providers:Fake:TranscriptionModel"] = "fake",
        };

        foreach (var folder in new[]
                 {
                     "DatabasePath", "DownloadPath", "UploadPath", "ProjectFilePath", "MeetingMediaPath",
                     "MeetingTranscriptPath", "AiChatPath", "DataProtectionKeyPath", "ExceptionPath", "AiCallLogPath",
                 })
        {
            settings[$"SystemSettings:ExternalFileSystem:{folder}"] = folder == "DatabasePath"
                ? DatabasePath
                : Path.Combine(rootPath, folder);
        }

        return settings.Select(pair => $"--{pair.Key}={pair.Value}");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EAppFixture>
{
    public const string Name = "E2E";
}
