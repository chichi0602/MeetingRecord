using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Models.Systems;
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
        SystemStartupState startupState)
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
    }

    public async Task<SystemHealthReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<SystemHealthItem>
        {
            CheckApplication(),
            CheckApi(),
            await CheckDatabaseAsync(cancellationToken),
            CheckLogging(),
            CheckAuthentication(),
            CheckFileSystem(),
            CheckHostResources(),
            CheckSecuritySettings()
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);
        var logTail = logReader.ReadLatestLines(100);

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
            10,
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
            10,
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
                    25,
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
                25,
                status,
                $"Provider：SQLite；可連線；待套用 migration：{pendingMigrations}。",
                pendingMigrations == 0 ? null : "仍有尚未套用的 EF Core migration。");
        }
        catch (Exception ex)
        {
            return CreateItem(
                "資料庫",
                "Database",
                25,
                SystemHealthStatus.Unhealthy,
                $"Provider：SQLite；檢查時發生 {ex.GetType().Name}。",
                $"資料庫檢查失敗：{ex.GetType().Name}。");
        }
    }

    private SystemHealthItem CheckLogging()
    {
        var logTail = logReader.ReadLatestLines(100);
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
            15,
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
            15,
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
            ["ProjectFile"] = systemSettingsOptions.Value.ExternalFileSystem.ProjectFilePath
        };

        var failures = paths
            .Where(path => string.IsNullOrWhiteSpace(path.Value) || !Directory.Exists(path.Value) || !DirectoryIsWritable(path.Value))
            .Select(path => path.Key)
            .ToList();
        var status = failures.Count == 0 ? SystemHealthStatus.Healthy : SystemHealthStatus.Unhealthy;

        return CreateItem(
            "檔案系統",
            "FileSystem",
            10,
            status,
            string.Join("；", paths.Select(path => $"{path.Key}：{path.Value}")),
            status == SystemHealthStatus.Healthy ? null : $"目錄不存在或不可寫入：{string.Join(", ", failures)}。");
    }

    private SystemHealthItem CheckHostResources()
    {
        var process = Process.GetCurrentProcess();
        var currentDriveRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        var drive = string.IsNullOrWhiteSpace(currentDriveRoot)
            ? null
            : DriveInfo.GetDrives().FirstOrDefault(item => string.Equals(item.Name, currentDriveRoot, StringComparison.OrdinalIgnoreCase));
        var freeGb = drive is null ? 0 : drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        var status = drive is null || freeGb >= 1
            ? SystemHealthStatus.Healthy
            : SystemHealthStatus.Degraded;

        return CreateItem(
            "主機資源",
            "Host",
            5,
            status,
            $"Working set：{process.WorkingSet64 / 1024 / 1024} MB；磁碟可用空間：{freeGb:N2} GB。",
            status == SystemHealthStatus.Healthy ? null : "目前磁碟可用空間低於 1 GB。");
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
            10,
            status,
            $"Swagger.EnabledInProduction：{swaggerSettings.EnabledInProduction}；CORS origins：{corsSettings.AllowedOrigins.Length}；ReturnExceptionDetails：{returnExceptionDetails?.ToString() ?? "null"}。",
            status == SystemHealthStatus.Healthy ? null : "Production 開啟了診斷或 Swagger 設定，請確認是否符合部署政策。");
    }

    private static SystemHealthItem CreateItem(
        string name,
        string category,
        int weight,
        SystemHealthStatus status,
        string evidence,
        string? failureMessage)
    {
        return new SystemHealthItem
        {
            Name = name,
            Category = category,
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
