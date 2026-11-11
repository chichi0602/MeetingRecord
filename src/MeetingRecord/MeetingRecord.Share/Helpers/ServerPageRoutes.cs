namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 仍由伺服器靜態算繪（static SSR）的頁面路徑（1.1.45 起；取代遷移期間的 <c>WasmRoutes</c>）。
///
/// <para>
/// WASM 遷移 M5（1.1.43）後應用程式的頁面全部在瀏覽器執行，M6（1.1.45）首頁 <c>/</c> 與 <c>/not-found</c> 也搬過去，
/// 伺服器只剩登入、登出、等待核准（<c>/Auths/*</c>，要寫 Cookie）與 <c>/Error</c>（例外處理頁）。
/// 這裡列的以外一律是 WASM 頁；往這些路徑要整頁載入（兩邊的 Router 都依這份判斷）。
/// </para>
/// </summary>
public static class ServerPageRoutes
{
    private const string AuthsPrefix = "/Auths/";
    private const string ErrorPath = "/Error";

    /// <param name="path">
    /// 以 <c>/</c> 開頭的路徑；查詢字串與錨點會被忽略（Router 的 <c>NavigationContext.Path</c> 帶著查詢字串，見 1.1.39）。
    /// </param>
    public static bool IsServerPage(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var end = path.IndexOfAny(['?', '#']);
        var pathOnly = end >= 0 ? path[..end] : path;
        pathOnly = pathOnly.Length > 1 ? pathOnly.TrimEnd('/') : pathOnly;
        return pathOnly.StartsWith(AuthsPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(pathOnly, ErrorPath, StringComparison.OrdinalIgnoreCase);
    }
}
