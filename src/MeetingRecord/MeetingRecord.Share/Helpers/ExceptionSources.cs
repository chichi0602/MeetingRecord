namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 例外的來源分類。刻意用字串常數而非 enum —— 值會直接寫進資料庫並顯示在畫面上，
/// 用 enum 還要多一層翻譯。
/// 1.1.40 從 <c>Models/Systems/ExceptionLogEntry.cs</c> 搬到 Share：WASM 版系統例外頁的來源下拉也要用。
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
