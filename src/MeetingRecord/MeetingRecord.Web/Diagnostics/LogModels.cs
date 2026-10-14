namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 日誌等級序位。數值越大越嚴重，用於「最低等級」篩選比較。
/// 0.4.120 移植自 NET10-Blazor-Starter。
/// </summary>
public enum LogLevelRank
{
    /// <summary>不限，不套用等級篩選。</summary>
    Any = -1,
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
    /// <summary>無法解析的等級。一律保留，不因等級篩選而隱藏資料。</summary>
    Unknown = 99,
}

/// <summary>
/// 日誌等級的字串與序位轉換。
///
/// Trace..Fatal 的序位 0-5 與 NLog LogLevel.Ordinal 完全一致，因此
/// NLog.LogLevel.FromOrdinal((int)rank) 是精確的橋接，不需字串剖析。
/// 但 Any(-1) 與 Unknown(99) 不在該範圍內，轉換前必須擋掉。
/// </summary>
public static class LogLevelRankHelper
{
    /// <summary>
    /// 由日誌檔中的等級字串解析序位。
    /// </summary>
    /// <param name="fallback">
    /// 無法解析時的回傳值。解析日誌檔時用 Unknown（保留該筆、不因等級篩選而隱藏）；
    /// 解析使用者的篩選輸入時用 Any（空字串代表「不限」）。
    /// </param>
    public static LogLevelRank FromLevelText(string level, LogLevelRank fallback) => level switch
    {
        "TRACE" => LogLevelRank.Trace,
        "DEBUG" => LogLevelRank.Debug,
        "INFO" => LogLevelRank.Info,
        "WARN" => LogLevelRank.Warn,
        "ERROR" => LogLevelRank.Error,
        "FATAL" => LogLevelRank.Fatal,
        _ => fallback,
    };

    /// <summary>是否為可對應到 NLog LogLevel 的實際等級（排除 Any 與 Unknown）。</summary>
    public static bool IsRealLevel(LogLevelRank rank)
        => rank >= LogLevelRank.Trace && rank <= LogLevelRank.Fatal;

    /// <summary>NLog 寫進日誌檔的大寫字串，例如 INFO。</summary>
    public static string ToLevelText(LogLevelRank rank) => rank switch
    {
        LogLevelRank.Trace => "TRACE",
        LogLevelRank.Debug => "DEBUG",
        LogLevelRank.Info => "INFO",
        LogLevelRank.Warn => "WARN",
        LogLevelRank.Error => "ERROR",
        LogLevelRank.Fatal => "FATAL",
        _ => string.Empty,
    };
}
