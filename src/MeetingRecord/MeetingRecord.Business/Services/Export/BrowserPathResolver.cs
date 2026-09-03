using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 找出可用來產生 PDF 的瀏覽器執行檔。
///
/// <para>
/// 設定留空時自動偵測——這個系統假設部署在 Windows，而 Windows 內建 Edge，
/// 絕大多數情況不需要任何設定就能運作。設定值存在時一律以設定為準，不做偵測。
/// </para>
/// </summary>
public static class BrowserPathResolver
{
    /// <summary>設定留空時依序嘗試的安裝位置。Edge 優先——Windows 一定有。</summary>
    public static readonly IReadOnlyList<string> WellKnownPaths =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ];

    /// <summary>
    /// 解析出實際要用的執行檔路徑；找不到時回傳 null。
    /// </summary>
    /// <param name="configuredPath">設定值，可為空。</param>
    /// <param name="exists">存在性判斷，開放覆寫是為了讓測試不依賴執行機器上真的裝了瀏覽器。</param>
    public static string? Resolve(string? configuredPath, Func<string, bool>? exists = null)
    {
        var probe = exists ?? ExecutablePathResolver.Exists;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // 設定值優先，且不回退到自動偵測——設錯了要讓使用者看到錯誤，而不是靜默用別的瀏覽器。
            return probe(configuredPath.Trim()) ? configuredPath.Trim() : null;
        }

        return WellKnownPaths.FirstOrDefault(probe);
    }
}
