namespace MeetingRecord.Models.Systems;

/// <summary>
/// 匯出設定。會議紀錄匯出 PDF 是以無頭瀏覽器列印產生的，
/// 因此瀏覽器執行檔位置必須可由設定調整（不同機器的安裝路徑不同）。
/// </summary>
public class ExportSettings
{
    public const string SectionName = "ExportSettings";

    /// <summary>
    /// 用來產生 PDF 的瀏覽器執行檔完整路徑（Edge 或 Chrome）；若已加入 PATH 也可以只填檔名。
    /// <para>留空時自動偵測常見的安裝位置，見 <c>BrowserPathResolver</c>。</para>
    /// </summary>
    public string BrowserPath { get; set; } = string.Empty;
}
