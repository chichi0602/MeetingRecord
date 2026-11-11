using System.Text;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Helps;

/// <summary>
/// 使用說明的內容（1.1.29 從 Server 的 <c>HelpView</c> 抽出）。1.1.30 起頁面是 WASM，只有 <c>api/v1/help</c> 用它。Singleton，讀一次就快取。
///
/// <para>
/// 內容來源是 <c>docs/guides/系統使用說明.md</c>，建置時以內嵌資源放進 Web 組件（見 <c>MeetingRecord.Web.csproj</c>）。
/// ⚠️ 刻意<b>不</b>從檔案系統讀 <c>docs/</c>：那個目錄不會被發佈到輸出目錄，會變成「開發時看得到、上線後 404」。
/// </para>
/// </summary>
public sealed class HelpManualProvider(ILogger<HelpManualProvider> logger)
{
    /// <summary>內嵌資源的名稱。與 csproj 的 <c>LogicalName</c> 必須一致——改了一邊就會在執行期變成載入失敗。</summary>
    public const string ResourceName = "MeetingRecord.Web.系統使用說明.md";

    /// <summary>讀不到時給使用者看的訊息（畫面與 API 共用）。</summary>
    public const string LoadFailedMessage = "使用說明載入失敗，請聯絡系統管理員。";

    private readonly Lazy<(string Markdown, HelpManual Manual)?> loaded = new(() => Load(logger));

    /// <summary>去掉檔頭的說明 Markdown（保留分類標記）；讀不到時是 null。</summary>
    public string? Markdown => loaded.Value?.Markdown;

    /// <summary>解析好的說明；讀不到時是 null。</summary>
    public HelpManual? Manual => loaded.Value?.Manual;

    private static (string Markdown, HelpManual Manual)? Load(ILogger logger)
    {
        var assembly = typeof(HelpManualProvider).Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // 這是建置設定壞掉，不是使用者的問題——要留下線索，不要只顯示空白頁。
            logger.LogError(
                "Embedded manual not found. ResourceName={ResourceName}, Available={Available}",
                ResourceName,
                string.Join("、", assembly.GetManifestResourceNames()));

            return null;
        }

        // ⚠️ 一定要去掉 BOM：docs/*.md 一律含 BOM，不處理的話 Markdig 會把第一行的「# 標題」當成普通段落。
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var markdown = HelpManual.StripDocumentFrontMatter(reader.ReadToEnd());
        var manual = HelpManual.Parse(markdown);

        // 標記漏了或打錯不會壞畫面（歸到第一類），但會讓章節跑錯分類——留線索給維護者，測試也會擋。
        if (manual.UnknownMarkers.Count > 0)
        {
            logger.LogWarning(
                "Help manual sections without a valid category marker: {Sections}",
                string.Join("、", manual.UnknownMarkers));
        }

        return (markdown, manual);
    }
}
