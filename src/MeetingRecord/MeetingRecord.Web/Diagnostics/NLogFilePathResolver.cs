namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 解析 NLog 檔案目標的實際路徑。
///
/// 路徑規則來自 nlog.config 的 <c>${var:BasePath}/${var:LogFilenamePrefix}-${shortdate}.log</c>，
/// 而兩個變數由 Program.cs 在啟動時以 <c>NLog:BasePath</c> 組態與 Program 命名空間注入。
/// 此類別集中該規則，避免在多處各寫一份。
///
/// 註：Program.cs 內另有一份相同推導，因其執行時機早於 DI 容器建立且帶有建立目錄等副作用，
/// 刻意不改動。0.4.120 移植自 NET10-Blazor-Starter。
/// </summary>
public interface INLogFilePathResolver
{
    /// <returns>日誌目錄；回傳空字串代表 NLog:BasePath 未設定。</returns>
    string GetLogDirectory();

    /// <returns>指定日期的日誌檔完整路徑；目錄未設定時回傳空字串。</returns>
    string GetLogFilePath(DateOnly date);

    /// <summary>
    /// 取得涵蓋 <paramref name="from"/> 至 <paramref name="to"/>（含）各日期、實際存在的日誌檔。
    /// </summary>
    /// <remarks>
    /// 以萬用字元列舉而非精確檔名：nlog 設定了 archiveAboveSize 卻未指定 archiveFileName，
    /// 同一天可能另存在編號封存檔，其命名格式隨 nlog 版本而異，不宜臆測。
    /// 排序依 LastWriteTimeUtc 而非檔名 —— 封存序號 10 與 2 的字典序會錯。
    /// </remarks>
    IReadOnlyList<string> GetExistingFilesInRange(DateOnly from, DateOnly to);
}

public sealed class NLogFilePathResolver : INLogFilePathResolver
{
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;

    public NLogFilePathResolver(IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
    }

    public string GetLogDirectory()
    {
        var basePathPrefix = configuration.GetValue<string>("NLog:BasePath");
        if (string.IsNullOrWhiteSpace(basePathPrefix))
        {
            return string.Empty;
        }

        return Path.Combine(basePathPrefix, GetBaseNamespace());
    }

    public string GetLogFilePath(DateOnly date)
    {
        var directory = GetLogDirectory();
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        return Path.Combine(directory, $"{GetFileNamePrefix()}-{date:yyyy-MM-dd}.log");
    }

    public IReadOnlyList<string> GetExistingFilesInRange(DateOnly from, DateOnly to)
    {
        var directory = GetLogDirectory();
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory) == false)
        {
            return [];
        }

        var prefix = GetFileNamePrefix();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var pattern = $"{prefix}-{date:yyyy-MM-dd}*.log";
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                matched.Add(file);
            }
        }

        return matched
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private string GetBaseNamespace()
        => typeof(Program).Namespace ?? environment.ApplicationName;

    private string GetFileNamePrefix()
        => $"{GetBaseNamespace()}-logfile";
}
