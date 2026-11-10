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
    };

    /// <param name="path">以 <c>/</c> 開頭、不含查詢字串的路徑。</param>
    public static bool IsWasm(string? path)
        => !string.IsNullOrEmpty(path) && Paths.Contains(path.Length > 1 ? path.TrimEnd('/') : path);
}
