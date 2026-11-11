namespace MeetingRecord.Share.Helpers;

/// <summary>
/// WASM 遷移期間（1.1.1 起）已經改由 Blazor WebAssembly 執行的頁面路徑。
///
/// <para>
/// 逐頁切換：在這份清單裡的路徑由伺服器以 InteractiveWebAssembly 模式送出、在瀏覽器執行；其餘仍是 Blazor Server 頁。
/// 兩種頁面之間切換一律整頁載入（兩邊的 Router 都依這份清單判斷）。每搬好一頁就加一行。
/// </para>
/// </summary>
public static class WasmRoutes
{
    private static readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/wasm",
        "/categories", // 1.1.7 分類清單
        "/teams", // 1.1.8 團隊清單
        "/prompttemplates", // 1.1.9 提示詞清單
        "/myusers", // 1.1.10 使用者管理
        "/roleviews", // 1.1.11 角色管理
        "/dashboard", // 1.1.12 儀表板
        "/ChangePassword", // 1.1.13 變更密碼（比對不分大小寫）
        "/Profile", // 1.1.13 設定 API 密碼
        "/todos", // 1.1.19 待辦事項
        "/meetings", // 1.1.23 會議紀錄
        "/projects", // 1.1.26 專案項目
        "/help", // 1.1.30 使用說明
        "/database-usage", // 1.1.32 資料庫用量
        "/log-level-setting", // 1.1.33 日誌等級設定
        "/system-health", // 1.1.35 系統健康監控
        "/token-usage", // 1.1.37 Token 用量
        "/ai-usage", // 1.1.37 Token 用量（1.0.30 前的舊網址）
        "/ai-call-logs", // 1.1.39 AI 對話紀錄
    };

    /// <param name="path">
    /// 以 <c>/</c> 開頭的路徑；查詢字串與錨點會被忽略（1.1.39）——Router 的 <c>NavigationContext.Path</c> 帶著查詢字串，
    /// 以前 <c>/ai-call-logs?callId=…</c> 被當成 Server 頁，WASM 端強制整頁重載、伺服器又送回 WASM 頁，無限重載。
    /// </param>
    public static bool IsWasm(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var end = path.IndexOfAny(['?', '#']);
        var pathOnly = end >= 0 ? path[..end] : path;
        return Paths.Contains(pathOnly.Length > 1 ? pathOnly.TrimEnd('/') : pathOnly);
    }
}
