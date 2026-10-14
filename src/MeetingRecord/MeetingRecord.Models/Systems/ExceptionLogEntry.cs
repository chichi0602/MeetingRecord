namespace MeetingRecord.Models.Systems;

/// <summary>
/// 例外記錄管線在「生產端」（ILoggerProvider）與「消費端」（背景寫入器 → 服務層）之間傳遞的資料。
///
/// 放在 MeetingRecord.Models 是因為 Web（生產者）與 Business（消費者）都要看得到它，
/// 而 Models 不相依任何其他專案，符合分層規範。
/// </summary>
public sealed class ExceptionLogEntry
{
    /// <summary>例外型別全名。</summary>
    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>ex.Message（已截斷）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>ex.ToString() 全文，寫入堆疊檔用。不進資料庫。</summary>
    public string StackTrace { get; init; } = string.Empty;

    /// <summary>來源，見 <see cref="ExceptionSources"/>。</summary>
    public string Source { get; init; } = ExceptionSources.Unknown;

    public string? Page { get; init; }

    /// <summary>
    /// 操作＝日誌訊息「樣板」（{OriginalFormat}），例如
    /// <c>Failed to create category. Name={CategoryName}</c>。
    /// ⚠️ 絕不可放算好的訊息，否則每個參數值都會變成一個新簽章。
    /// </summary>
    public string? Operation { get; init; }

    public string? LoggerName { get; init; }

    public string? Account { get; init; }

    public int? UserId { get; init; }

    public DateTime OccurredAt { get; init; } = DateTime.Now;

    /// <summary>錯誤追蹤碼（8 碼，見 TraceCode）；沒有請求或互動範圍時為 null。</summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// 是否為 Critical（應用程式無法繼續）。本專案沒有 Email 告警，目前只是保留資訊。
    /// 補登檔（程序結束、啟動失敗）一律為 true。
    /// </summary>
    public bool IsCritical { get; init; }
}

/// <summary>
/// <c>ExceptionLogService.RecordAsync</c> 的結果，寫入器以 null 判斷寫入失敗（計入 LoggingPipelineMonitor）。
/// </summary>
public sealed record ExceptionRecordOutcome(
    int Id,
    bool IsNew,
    bool IsOverflow,
    string ExceptionType,
    string Source,
    string? Page,
    long OccurrenceCount,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt,
    string? TraceId,
    bool IsCritical);

/// <summary>
/// 例外的來源分類。刻意用字串常數而非 enum —— 值會直接寫進資料庫並顯示在畫面上，
/// 用 enum 還要多一層翻譯。
/// </summary>
public static class ExceptionSources
{
    /// <summary>Blazor 畫面互動，或非 /api 的 HTTP 請求。</summary>
    public const string Ui = "畫面";

    /// <summary>路徑以 /api 開頭的 Web API 請求。</summary>
    public const string WebApi = "WebAPI";

    /// <summary>系統啟動流程（migration、RBAC 回填等）。</summary>
    public const string Startup = "系統啟動";

    /// <summary>背景作業（Email 派送、AI 對話紀錄自動過期等 BackgroundService）。</summary>
    public const string Background = "背景作業";

    /// <summary>
    /// 程序層級：沒人 await 的失敗 Task（UnobservedTaskException）與
    /// 其他執行緒的未處理例外（AppDomain.UnhandledException）。見 ProcessExceptionHooks。
    /// </summary>
    public const string Process = "系統";

    /// <summary>瀏覽器端的 JavaScript 錯誤（見 BrowserErrorReporter）。</summary>
    public const string Browser = "瀏覽器";

    /// <summary>沒有任何情境資訊可用時的預設值。</summary>
    public const string Unknown = "未知";

    public static IReadOnlyList<string> All { get; } =
    [
        Ui,
        WebApi,
        Startup,
        Background,
        Process,
        Browser,
        Unknown,
    ];
}
