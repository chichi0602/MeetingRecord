// LogLevel 在 Microsoft.Extensions.Logging 與 NLog 兩個命名空間都存在，
// 因此不 using NLog 而改用別名，避免每個型別都要完整限定。
using NLog.Config;
using NLogLevel = NLog.LogLevel;
using NLogManager = NLog.LogManager;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 執行期的 NLog 最低等級狀態。
///
/// 只改 nlog.config 的 &lt;logger name="*"&gt; 規則，不寫回任何檔案 —— 重新啟動就會回到
/// 系統預設等級。前面幾條 Microsoft.* / System.* 規則設了 final="true"，所以調低等級
/// 只會讓「應用程式日誌」變多，不會引來框架日誌洪水；反過來說，調高等級也無法靜音
/// 那些框架規則。這個不對稱是刻意保留的，畫面上有說明。
///
/// 鎖的責任完全在這裡：LoggingRules 是普通的 IList，改動發生在 Blazor circuit
/// 執行緒上，而其他執行緒同時正在寫日誌。
///
/// 0.4.120 移植自 NET10-Blazor-Starter（「監控與診斷 → 日誌等級設定」）。
/// </summary>
public sealed class LogLevelRuntimeState : IDisposable
{
    /// <summary>nlog.config 中套用於應用程式日誌的萬用規則。</summary>
    private const string ApplicationLoggerPattern = "*";

    private readonly ILogger<LogLevelRuntimeState> logger;
    private readonly Lock gate = new();

    private LogLevelRank systemDefaultLevel = LogLevelRank.Info;
    private LogLevelRank? overrideLevel;
    private bool initialized;

    /// <summary>重載時要補回去的 NLog 變數，避免檔案輸出路徑遺失。</summary>
    private readonly Dictionary<string, string> preservedVariables = new(StringComparer.Ordinal);

    public LogLevelRuntimeState(ILogger<LogLevelRuntimeState> logger)
    {
        this.logger = logger;
    }

    /// <summary>來自 nlog.config 的等級；nlog.config 被重載時會刷新。</summary>
    public LogLevelRank SystemDefaultLevel
    {
        get { lock (gate) { return systemDefaultLevel; } }
    }

    /// <summary>執行期覆寫；null 代表未套用。</summary>
    public LogLevelRank? OverrideLevel
    {
        get { lock (gate) { return overrideLevel; } }
    }

    /// <summary>目前實際生效的等級。</summary>
    public LogLevelRank EffectiveLevel
    {
        get { lock (gate) { return overrideLevel ?? systemDefaultLevel; } }
    }

    /// <summary>是否成功掛上 NLog 設定（讀不到設定或找不到萬用規則時為 false）。</summary>
    public bool IsAvailable
    {
        get { lock (gate) { return initialized; } }
    }

    /// <summary>
    /// 在應用程式啟動時呼叫一次。
    ///
    /// 必須是啟動時而非首次開啟頁面時 —— 底下的 ConfigurationChanged 訂閱同時負責修復
    /// 一個既有缺陷：nlog.config 設了 autoReload="true"，重載時 NLog 會整個換掉
    /// NLogManager.Configuration，連帶清空 Program.cs 設定的 BasePath / LogFilenamePrefix
    /// 變數，導致日誌從此寫到磁碟根目錄而非設定的目錄。若懶載入，沒人開頁面就不會修復。
    /// </summary>
    public void Initialize()
    {
        lock (gate)
        {
            if (initialized)
            {
                return;
            }

            var configuration = NLogManager.Configuration;
            if (configuration is null)
            {
                logger.LogWarning("NLog configuration is not available; runtime log level control is disabled.");
                return;
            }

            CaptureVariables(configuration);

            var rule = FindApplicationRule(configuration);
            if (rule is null)
            {
                logger.LogWarning(
                    "Could not find the NLog rule for logger pattern '{Pattern}'; runtime log level control is disabled.",
                    ApplicationLoggerPattern);
                return;
            }

            systemDefaultLevel = ReadMinimumLevel(rule);
            initialized = true;

            NLogManager.ConfigurationChanged += OnConfigurationChanged;

            logger.LogInformation(
                "Runtime log level control initialized. SystemDefault={SystemDefault}",
                LogLevelRankHelper.ToLevelText(systemDefaultLevel));
        }
    }

    /// <summary>套用執行期等級。</summary>
    /// <returns>成功時回傳 true。</returns>
    public bool Apply(LogLevelRank level)
    {
        if (LogLevelRankHelper.IsRealLevel(level) == false)
        {
            return false;
        }

        lock (gate)
        {
            if (initialized == false)
            {
                return false;
            }

            if (ApplyToConfiguration(level) == false)
            {
                return false;
            }

            overrideLevel = level;
            logger.LogInformation(
                "Runtime log level applied. Level={Level}", LogLevelRankHelper.ToLevelText(level));
            return true;
        }
    }

    /// <summary>丟棄執行期覆寫，回到 nlog.config 的等級。</summary>
    public bool RestoreDefault()
    {
        lock (gate)
        {
            if (initialized == false || overrideLevel is null)
            {
                return false;
            }

            if (ApplyToConfiguration(systemDefaultLevel) == false)
            {
                return false;
            }

            overrideLevel = null;
            logger.LogInformation(
                "Runtime log level restored to system default. Level={Level}",
                LogLevelRankHelper.ToLevelText(systemDefaultLevel));
            return true;
        }
    }

    /// <summary>
    /// nlog.config 被重載時：先把檔案的新值讀進系統預設等級（此時設定尚未被我們動過），
    /// 再補回被清掉的變數，最後把執行期覆寫重新套上去。
    /// </summary>
    private void OnConfigurationChanged(object? sender, LoggingConfigurationChangedEventArgs args)
    {
        lock (gate)
        {
            var configuration = NLogManager.Configuration;
            if (configuration is null)
            {
                return;
            }

            RestoreVariables(configuration);

            var rule = FindApplicationRule(configuration);
            if (rule is null)
            {
                logger.LogWarning("NLog configuration reloaded but the application logger rule is missing.");
                return;
            }

            systemDefaultLevel = ReadMinimumLevel(rule);

            if (overrideLevel is not null)
            {
                ApplyToConfiguration(overrideLevel.Value);
            }

            logger.LogInformation(
                "NLog configuration reloaded. SystemDefault={SystemDefault}, Override={Override}",
                LogLevelRankHelper.ToLevelText(systemDefaultLevel),
                overrideLevel is null ? "(none)" : LogLevelRankHelper.ToLevelText(overrideLevel.Value));
        }
    }

    /// <summary>呼叫端必須已持有 gate。</summary>
    private bool ApplyToConfiguration(LogLevelRank level)
    {
        var configuration = NLogManager.Configuration;
        var rule = configuration is null ? null : FindApplicationRule(configuration);
        if (rule is null)
        {
            return false;
        }

        rule.SetLoggingLevels(NLogLevel.FromOrdinal((int)level), NLogLevel.Fatal);

        // 沒有這一行，已建立的 Logger 會沿用快取的等級過濾器，設定等於沒生效 ——
        // 而執行中的應用程式，每一個 Logger 都是已建立的。
        NLogManager.ReconfigExistingLoggers();
        return true;
    }

    private static LoggingRule? FindApplicationRule(LoggingConfiguration configuration)
        => configuration.LoggingRules
            .FirstOrDefault(rule =>
                rule.LoggerNamePattern == ApplicationLoggerPattern && rule.Targets.Count > 0);

    private static LogLevelRank ReadMinimumLevel(LoggingRule rule)
    {
        var levels = rule.Levels;
        if (levels.Count == 0)
        {
            // 規則沒有啟用任何等級（等同關閉）。回傳最嚴格的實際等級作為安全退化，
            // 避免畫面顯示成「Trace」而誤導使用者以為正在記錄一切。
            return LogLevelRank.Fatal;
        }

        return (LogLevelRank)levels.Min(level => level.Ordinal);
    }

    private void CaptureVariables(LoggingConfiguration configuration)
    {
        foreach (var pair in configuration.Variables)
        {
            preservedVariables[pair.Key] = pair.Value?.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// 解除 ConfigurationChanged 訂閱。
    ///
    /// 正式環境中這是 Singleton、生命週期等同行程，但沒有解除訂閱等於把自己永久掛在
    /// 一個靜態事件上 —— 在測試中會造成前一個實例仍在監聽、並把它的覆寫套到後續測試
    /// 建立的設定上（實測會讓測試單獨跑會過、一起跑就失敗）。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (initialized == false)
            {
                return;
            }

            NLogManager.ConfigurationChanged -= OnConfigurationChanged;
            initialized = false;
        }
    }

    private void RestoreVariables(LoggingConfiguration configuration)
    {
        foreach (var pair in preservedVariables)
        {
            configuration.Variables[pair.Key] = pair.Value;
        }
    }
}
