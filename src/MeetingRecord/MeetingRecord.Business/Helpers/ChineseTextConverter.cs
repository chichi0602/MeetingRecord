using OpenCCNET;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 簡體轉繁體（台灣字形），1.0.41。
///
/// <para>
/// 語音轉錄（Azure gpt-transcribe）直接輸出簡體字，提示詞管不到它；使用者要求系統裡不要出現簡體字。
/// 逐字稿在寫入（<c>TranscriptionJobRunner</c>）與讀取（<c>MeetingFileStore.ReadTranscriptAsync</c>）都經過這裡：
/// 新的存檔就是繁體，舊檔不必重新轉錄也一律顯示繁體。
/// </para>
///
/// <para>
/// ⚠️ <b>只轉字形，不轉用詞</b>（使用者決定）：<c>HansToTW(text, false)</c>。轉用詞會把「软件」換成「軟體」，
/// 讀起來順，但也會把原話改掉（例如「文件」被換成「檔案」），逐字稿要忠於講者說的話。
/// </para>
///
/// <para>
/// ⚠️ 不要拿來轉 AI 產生的會議紀錄、問答、待辦：那些已由提示詞要求繁體，而且輸入（逐字稿）是繁體，
/// 對已是繁體的長文再轉一次只會增加誤換的機會。
/// </para>
/// </summary>
public static class ChineseTextConverter
{
    /// <summary>
    /// 字典只載入一次。用絕對路徑：部署在 IIS 時工作目錄不是程式目錄，套件預設的相對路徑會找不到字典。
    /// 分詞用 OpenCC 原版的最大匹配（MaxMatch），不必載入 Jieba 的資源。
    /// </summary>
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        ZhConverter.Initialize(
            dictionaryDirectory: Path.Combine(AppContext.BaseDirectory, "Dictionary"),
            jiebaResourceDirectory: Path.Combine(AppContext.BaseDirectory, "JiebaResource"),
            segmentMode: SegmentMode.MaxMatch);
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>簡體 → 繁體（台灣字形，不轉用詞）。null 或空白原樣回傳。</summary>
    public static string? ToTraditional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        _ = Initialized.Value;
        return ZhConverter.HansToTW(text, false);
    }
}
