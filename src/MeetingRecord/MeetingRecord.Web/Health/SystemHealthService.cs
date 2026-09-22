using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Health;

public interface ISystemHealthService
{
    Task<SystemHealthReport> GetReportAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemHealthService : ISystemHealthService
{
    private const string DevelopmentSigningKey = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars";
    private readonly BackendDBContext context;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly IActionDescriptorCollectionProvider actionDescriptorProvider;
    private readonly IOptions<AuthenticationOptions> authenticationOptions;
    private readonly IOptions<JwtSettings> jwtOptions;
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly IOptions<SwaggerSettings> swaggerOptions;
    private readonly IOptions<CorsSettings> corsOptions;
    private readonly IHealthLogReader logReader;
    private readonly SystemStartupState startupState;
    private readonly IOptions<LlmSettings> llmOptions;
    private readonly IOptions<MediaSettings> mediaOptions;
    private readonly IOptions<ExportSettings> exportOptions;
    private readonly IOptions<ExchangeRateSettings> exchangeRateOptions;
    private readonly ExchangeRateCache exchangeRateCache;
    private readonly ITranscriptionProgressNotifier transcriptionProgress;
    private readonly IMeetingDraftProgressNotifier draftProgress;

    public SystemHealthService(
        BackendDBContext context,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IActionDescriptorCollectionProvider actionDescriptorProvider,
        IOptions<AuthenticationOptions> authenticationOptions,
        IOptions<JwtSettings> jwtOptions,
        IOptions<SystemSettings> systemSettingsOptions,
        IOptions<SwaggerSettings> swaggerOptions,
        IOptions<CorsSettings> corsOptions,
        IHealthLogReader logReader,
        SystemStartupState startupState,
        IOptions<LlmSettings> llmOptions,
        IOptions<MediaSettings> mediaOptions,
        IOptions<ExportSettings> exportOptions,
        IOptions<ExchangeRateSettings> exchangeRateOptions,
        ExchangeRateCache exchangeRateCache,
        ITranscriptionProgressNotifier transcriptionProgress,
        IMeetingDraftProgressNotifier draftProgress)
    {
        this.context = context;
        this.configuration = configuration;
        this.environment = environment;
        this.actionDescriptorProvider = actionDescriptorProvider;
        this.authenticationOptions = authenticationOptions;
        this.jwtOptions = jwtOptions;
        this.systemSettingsOptions = systemSettingsOptions;
        this.swaggerOptions = swaggerOptions;
        this.corsOptions = corsOptions;
        this.logReader = logReader;
        this.startupState = startupState;
        this.llmOptions = llmOptions;
        this.mediaOptions = mediaOptions;
        this.exportOptions = exportOptions;
        this.exchangeRateOptions = exchangeRateOptions;
        this.exchangeRateCache = exchangeRateCache;
        this.transcriptionProgress = transcriptionProgress;
        this.draftProgress = draftProgress;
    }

    public async Task<SystemHealthReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        // 日誌只讀一次：「日誌」「近期錯誤」兩項與頁面下方的日誌尾端共用。
        var logTail = logReader.ReadLatestLines(100);

        var items = new List<SystemHealthItem>
        {
            CheckApplication(),
            CheckApi(),
            await CheckDatabaseAsync(cancellationToken),
            CheckLogging(logTail),
            CheckAuthentication(),
            CheckFileSystem(),
            CheckHostResources(),
            CheckSecuritySettings(),
            CheckAiProvider(),
            CheckTranscription(),
            await CheckBackgroundJobsAsync(cancellationToken),
            await CheckRecentErrorsAsync(logTail, cancellationToken),
            CheckPdfExport(),
            CheckExchangeRate()
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        return new SystemHealthReport
        {
            CheckedAt = DateTimeOffset.Now,
            Score = score,
            Status = SystemHealthScoreCalculator.GetStatus(score),
            Light = SystemHealthScoreCalculator.GetLight(score),
            Items = items,
            LogTail = logTail
        };
    }

    private SystemHealthItem CheckApplication()
    {
        var systemInfo = systemSettingsOptions.Value.SystemInformation;
        var uptime = DateTimeOffset.Now - startupState.StartedAt;

        return CreateItem(
            "網站 / 應用程式",
            "Application",
            SystemHealthWeights.Application,
            string.IsNullOrWhiteSpace(systemInfo.SystemVersion)
                ? SystemHealthStatus.Degraded
                : SystemHealthStatus.Healthy,
            $"環境：{environment.EnvironmentName}；版本：{systemInfo.SystemVersion}；啟動時間：{startupState.StartedAt:yyyy/MM/dd HH:mm:ss}；已運作：{uptime:g}。",
            string.IsNullOrWhiteSpace(systemInfo.SystemVersion) ? "SystemVersion 未設定。" : null);
    }

    private SystemHealthItem CheckApi()
    {
        var controllerCount = actionDescriptorProvider.ActionDescriptors.Items
            .Count(action => action.RouteValues.ContainsKey("controller"));
        var swaggerSettings = swaggerOptions.Value;
        var swaggerEvidence = environment.IsDevelopment() || swaggerSettings.EnabledInProduction
            ? "Swagger UI 依目前環境/設定可啟用"
            : "Swagger UI 在非開發環境預設關閉";

        var status = controllerCount > 0 ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "API",
            "API",
            SystemHealthWeights.Api,
            status,
            $"Controller action 數量：{controllerCount}；{swaggerEvidence}。",
            status == SystemHealthStatus.Healthy ? null : "找不到 Controller action 註冊。");
    }

    private async Task<SystemHealthItem> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var canConnect = await context.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                return CreateItem(
                    "資料庫",
                    "Database",
                    SystemHealthWeights.Database,
                    SystemHealthStatus.Unhealthy,
                    "Provider：SQLite；無法建立資料庫連線。",
                    "Database.CanConnectAsync 回傳 false。");
            }

            var pendingMigrations = context.Database.GetMigrations().Any()
                ? (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count()
                : 0;

            var status = pendingMigrations == 0
                ? SystemHealthStatus.Healthy
                : SystemHealthStatus.Degraded;

            return CreateItem(
                "資料庫",
                "Database",
                SystemHealthWeights.Database,
                status,
                $"Provider：SQLite；可連線；待套用 migration：{pendingMigrations}。",
                pendingMigrations == 0 ? null : "仍有尚未套用的 EF Core migration。");
        }
        catch (Exception ex)
        {
            return CreateItem(
                "資料庫",
                "Database",
                SystemHealthWeights.Database,
                SystemHealthStatus.Unhealthy,
                $"Provider：SQLite；檢查時發生 {ex.GetType().Name}。",
                $"資料庫檢查失敗：{ex.GetType().Name}。");
        }
    }

    private SystemHealthItem CheckLogging(HealthLogTail logTail)
    {
        var logDirectory = string.IsNullOrWhiteSpace(logTail.FilePath)
            ? string.Empty
            : Path.GetDirectoryName(logTail.FilePath) ?? string.Empty;
        var directoryWritable = DirectoryIsWritable(logDirectory);
        var status = logTail.Status;

        if (!directoryWritable)
        {
            status = SystemHealthStatus.Unhealthy;
        }
        else if (status == SystemHealthStatus.Healthy && logTail.Lines.Count == 0)
        {
            status = SystemHealthStatus.Degraded;
        }

        return CreateItem(
            "日誌",
            "Logging",
            SystemHealthWeights.Logging,
            status,
            $"目錄：{(string.IsNullOrWhiteSpace(logDirectory) ? "未設定" : logDirectory)}；今日檔案：{logTail.FilePath}；最後讀取筆數：{logTail.Lines.Count}。",
            status == SystemHealthStatus.Healthy ? null : logTail.Message);
    }

    private SystemHealthItem CheckAuthentication()
    {
        var jwtSettings = jwtOptions.Value;
        var schemes = authenticationOptions.Value.Schemes.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);
        var hasCookie = schemes.Contains(MagicObjectHelper.CookieScheme);
        var hasJwt = schemes.Contains(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        var hasRequiredJwtSettings = !string.IsNullOrWhiteSpace(jwtSettings.Issuer)
            && !string.IsNullOrWhiteSpace(jwtSettings.Audience)
            && !string.IsNullOrWhiteSpace(jwtSettings.SigningKey)
            && jwtSettings.SigningKey.Length >= 32;
        var usesDevelopmentKeyInProduction = environment.IsProduction()
            && string.Equals(jwtSettings.SigningKey, DevelopmentSigningKey, StringComparison.Ordinal);

        var status = hasCookie && hasJwt && hasRequiredJwtSettings && !usesDevelopmentKeyInProduction
            ? SystemHealthStatus.Healthy
            : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "身分驗證",
            "Authentication",
            SystemHealthWeights.Authentication,
            status,
            $"Cookie scheme：{hasCookie}；JWT bearer：{hasJwt}；Issuer：{MaskPresence(jwtSettings.Issuer)}；Audience：{MaskPresence(jwtSettings.Audience)}；SigningKey 長度：{jwtSettings.SigningKey.Length}。",
            status == SystemHealthStatus.Healthy ? null : "Cookie/JWT 設定不完整，或 Production 仍使用開發用 JWT key。");
    }

    private SystemHealthItem CheckFileSystem()
    {
        var paths = new Dictionary<string, string>
        {
            ["Database"] = systemSettingsOptions.Value.ExternalFileSystem.DatabasePath,
            ["Download"] = systemSettingsOptions.Value.ExternalFileSystem.DownloadPath,
            ["Upload"] = systemSettingsOptions.Value.ExternalFileSystem.UploadPath,
            ["ProjectFile"] = systemSettingsOptions.Value.ExternalFileSystem.ProjectFilePath,
            // 0.4.93 補上：影音、逐字稿、AI 問答。之前漏掉，這三個壞了健康頁卻是綠燈。
            ["MeetingMedia"] = systemSettingsOptions.Value.ExternalFileSystem.MeetingMediaPath,
            ["MeetingTranscript"] = systemSettingsOptions.Value.ExternalFileSystem.MeetingTranscriptPath,
            ["AiChat"] = systemSettingsOptions.Value.ExternalFileSystem.AiChatPath
        };

        var failures = paths
            .Where(path => string.IsNullOrWhiteSpace(path.Value) || !Directory.Exists(path.Value) || !DirectoryIsWritable(path.Value))
            .Select(path => path.Key)
            .ToList();
        var status = failures.Count == 0 ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "檔案系統",
            "FileSystem",
            SystemHealthWeights.FileSystem,
            status,
            string.Join("；", paths.Select(path => $"{path.Key}：{path.Value}")),
            status == SystemHealthStatus.Healthy ? null : $"目錄不存在或不可寫入：{string.Join(", ", failures)}。");
    }

    private SystemHealthItem CheckHostResources()
    {
        var process = Process.GetCurrentProcess();

        // 0.4.93 起量的是「資料目錄所在的磁碟」，不是程式所在的磁碟——錄音、逐字稿、資料庫
        // 放在另一顆磁碟時，之前量錯地方，資料碟滿了這裡還是綠燈。
        var fileSystem = systemSettingsOptions.Value.ExternalFileSystem;
        var roots = new[]
            {
                fileSystem.DatabasePath, fileSystem.DownloadPath, fileSystem.UploadPath, fileSystem.ProjectFilePath,
                fileSystem.MeetingMediaPath, fileSystem.MeetingTranscriptPath, fileSystem.AiChatPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetPathRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var disks = new List<string>();
        var lowDisks = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                var freeGb = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
                disks.Add($"{root} 剩 {freeGb:N2} GB");
                if (freeGb < 1)
                {
                    lowDisks.Add(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                disks.Add($"{root} 無法讀取");
            }
        }

        var status = lowDisks.Count == 0
            ? SystemHealthStatus.Healthy
            : SystemHealthStatus.Degraded;

        return CreateItem(
            "主機資源",
            "Host",
            SystemHealthWeights.Host,
            status,
            $"Working set：{process.WorkingSet64 / 1024 / 1024} MB；資料磁碟：{(disks.Count == 0 ? "未設定資料目錄" : string.Join("、", disks))}。",
            status == SystemHealthStatus.Healthy ? null : $"資料磁碟可用空間低於 1 GB：{string.Join("、", lowDisks)}。");
    }

    private SystemHealthItem CheckSecuritySettings()
    {
        var swaggerSettings = swaggerOptions.Value;
        var corsSettings = corsOptions.Value;
        var returnExceptionDetails = configuration.GetValue<bool?>("Security:ReturnExceptionDetails");
        var productionRisk = environment.IsProduction()
            && (swaggerSettings.EnabledInProduction || returnExceptionDetails == true);
        var status = productionRisk ? SystemHealthStatus.Degraded : SystemHealthStatus.Healthy;

        return CreateItem(
            "安全設定",
            "Security",
            SystemHealthWeights.Security,
            status,
            $"Swagger.EnabledInProduction：{swaggerSettings.EnabledInProduction}；CORS origins：{corsSettings.AllowedOrigins.Length}；ReturnExceptionDetails：{returnExceptionDetails?.ToString() ?? "null"}。",
            status == SystemHealthStatus.Healthy ? null : "Production 開啟了診斷或 Swagger 設定，請確認是否符合部署政策。");
    }

    private SystemHealthItem CheckAiProvider()
    {
        return CreateFeatureItem("AI 供應商設定", "AiProvider", SystemHealthWeights.AiProvider,
            SystemHealthChecks.EvaluateAiProvider(llmOptions.Value));
    }

    private SystemHealthItem CheckTranscription()
    {
        return CreateFeatureItem("語音轉錄（FFmpeg）", "Transcription", SystemHealthWeights.Transcription,
            SystemHealthChecks.EvaluateTranscription(
                llmOptions.Value.IsTranscriptionConfigured,
                mediaOptions.Value.FfmpegPath,
                FfmpegPathResolver.Exists));
    }

    private SystemHealthItem CheckPdfExport()
    {
        return CreateFeatureItem("PDF 匯出", "PdfExport", SystemHealthWeights.PdfExport,
            SystemHealthChecks.EvaluatePdfExport(exportOptions.Value.BrowserPath));
    }

    private SystemHealthItem CheckExchangeRate()
    {
        return CreateFeatureItem("匯率", "ExchangeRate", SystemHealthWeights.ExchangeRate,
            SystemHealthChecks.EvaluateExchangeRate(
                exchangeRateOptions.Value,
                exchangeRateCache.Current,
                exchangeRateCache.LastFailureAt,
                DateTime.Now));
    }

    private async Task<SystemHealthItem> CheckBackgroundJobsAsync(CancellationToken cancellationToken)
    {
        const string name = "背景工作";
        try
        {
            // ⚠️ 先抓通知器、再查資料庫：入列時是「先寫資料庫、再登錄通知器」，
            //    反過來查的話剛好卡在兩步之間的那件工作會被誤判成卡住。
            var transcriptions = transcriptionProgress.GetSnapshot().Where(x => x.IsRunning).Select(x => x.MeetingId).ToHashSet();
            var drafts = draftProgress.GetSnapshot().Where(x => x.IsRunning).Select(x => x.MeetingId).ToHashSet();

            var meetings = await context.Meeting
                .AsNoTracking()
                .Where(x => x.TranscriptionStatus == TranscriptionStatus.Pending
                         || x.TranscriptionStatus == TranscriptionStatus.Processing
                         || x.DraftStatus == DraftStatus.Pending
                         || x.DraftStatus == DraftStatus.Processing)
                .Select(x => new
                {
                    x.Id,
                    x.Title,
                    x.TranscriptionStatus,
                    x.TranscriptionStartedAt,
                    x.DraftStatus,
                    x.DraftStartedAt
                })
                .ToListAsync(cancellationToken);

            var jobs = new List<BackgroundJobRecord>();
            foreach (var meeting in meetings)
            {
                if (meeting.TranscriptionStatus is TranscriptionStatus.Pending or TranscriptionStatus.Processing)
                {
                    jobs.Add(new BackgroundJobRecord(BackgroundJobKind.Transcription, meeting.Id, meeting.Title,
                        meeting.TranscriptionStatus == TranscriptionStatus.Processing, meeting.TranscriptionStartedAt));
                }

                if (meeting.DraftStatus is DraftStatus.Pending or DraftStatus.Processing)
                {
                    jobs.Add(new BackgroundJobRecord(BackgroundJobKind.MeetingDraft, meeting.Id, meeting.Title,
                        meeting.DraftStatus == DraftStatus.Processing, meeting.DraftStartedAt));
                }
            }

            var stuck = SystemHealthChecks.FindStuckJobs(jobs, transcriptions, drafts, DateTime.Now);
            return CreateFeatureItem(name, "BackgroundJobs", SystemHealthWeights.BackgroundJobs,
                SystemHealthChecks.EvaluateBackgroundJobs(transcriptions.Count, drafts.Count, stuck));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateFeatureItem(name, "BackgroundJobs", SystemHealthWeights.BackgroundJobs,
                new SystemHealthVerdict(SystemHealthStatus.Unhealthy, $"檢查時發生 {ex.GetType().Name}。", "無法讀取背景工作狀態。"));
        }
    }

    private async Task<SystemHealthItem> CheckRecentErrorsAsync(HealthLogTail logTail, CancellationToken cancellationToken)
    {
        const string name = "近期錯誤";
        try
        {
            var since = DateTime.Now.AddHours(-24);

            // Outcome 是 int 欄位，GroupBy 在 SQLite 可以直接翻成 SQL（金額那種 TEXT 欄位才不行）。
            var outcomes = await context.AiUsageLog
                .AsNoTracking()
                .Where(x => x.OccurredAt >= since)
                .GroupBy(x => x.Outcome)
                .Select(g => new { Outcome = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var succeeded = outcomes.Where(x => x.Outcome == AiUsageOutcome.Succeeded).Sum(x => x.Count);
            var failed = outcomes.Where(x => x.Outcome == AiUsageOutcome.Failed).Sum(x => x.Count);

            return CreateFeatureItem(name, "RecentErrors", SystemHealthWeights.RecentErrors,
                SystemHealthChecks.EvaluateRecentErrors(succeeded, failed, logTail.ErrorCount));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateFeatureItem(name, "RecentErrors", SystemHealthWeights.RecentErrors,
                new SystemHealthVerdict(SystemHealthStatus.Unhealthy, $"檢查時發生 {ex.GetType().Name}。", "無法讀取 AI 用量紀錄。"));
        }
    }

    private static SystemHealthItem CreateFeatureItem(string name, string category, int weight, SystemHealthVerdict verdict)
    {
        return CreateItem(name, category, weight, verdict.Status, verdict.Evidence, verdict.FailureMessage, SystemHealthGroups.Features);
    }

    private static SystemHealthItem CreateItem(
        string name,
        string category,
        int weight,
        SystemHealthStatus status,
        string evidence,
        string? failureMessage,
        string group = SystemHealthGroups.Infrastructure)
    {
        return new SystemHealthItem
        {
            Name = name,
            Category = category,
            Group = group,
            Weight = weight,
            Status = status,
            Light = SystemHealthScoreCalculator.GetLight(status),
            Evidence = evidence,
            FailureMessage = failureMessage
        };
    }

    private static string MaskPresence(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未設定" : "已設定";
    }

    private static bool DirectoryIsWritable(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        try
        {
            var testFile = Path.Combine(directoryPath, $".health-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
