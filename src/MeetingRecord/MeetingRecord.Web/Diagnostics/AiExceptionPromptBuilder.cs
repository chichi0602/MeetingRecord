using System.Text;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 把例外明細組成要送給模型的第一則使用者訊息。純函式，無 I/O、無 DI。
/// </summary>
public static class AiExceptionPromptBuilder
{
    private const string StackStartMarker = "----- 完整堆疊開始 -----";
    private const string StackEndMarker = "----- 完整堆疊結束 -----";

    /// <summary>
    /// 送給 AI 的明細欄位。畫面對話窗、PDF 報告的「例外明細」共用這一份，
    /// 使用者在報告上看到的就是 AI 實際收到的內容。
    ///
    /// ⚠️ 個資紅線：<b>不含</b>帳號與 UserId，只給「有／無登入使用者」——
    /// 帳號對判斷根因幾乎沒有幫助，送出去卻是個資外洩。
    /// Signature 與 StackTraceFile 是內部鍵值，對分析沒有意義，也不送。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildDetailLines(ExceptionLogAdapterModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var hasUser = string.IsNullOrWhiteSpace(item.Account) == false || item.UserId.HasValue;

        return
        [
            new("例外類型", item.ExceptionType),
            new("訊息", item.Message),
            new("來源", item.Source),
            new("頁面", item.Page ?? "—"),
            new("操作（日誌訊息樣板）", item.Operation ?? "—"),
            new("記錄器", item.LoggerName ?? "—"),
            new("使用者", hasUser ? "有登入使用者" : "無登入使用者"),
            new("累計次數", item.OccurrenceCount.ToString("N0")),
            new("首次發生", item.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("最後發生", item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        ];
    }

    /// <summary>
    /// 組出第一則使用者訊息。堆疊全文送出、不截斷（堆疊尾巴常常才是根因所在）；
    /// 真的超過模型的內容視窗時，由共用核心回報 <c>context_length_exceeded</c>。
    ///
    /// 堆疊用文字標記包起來而不是 Markdown 程式碼區塊：堆疊內容本身可能含有反引號，
    /// 用 ``` 包會被提早閉合。
    /// </summary>
    public static string Build(ExceptionLogAdapterModel item, string? stackTrace)
    {
        // ⚠️ 一律用 '\n' 而不是 AppendLine：AppendLine 會用 Environment.NewLine，在 Windows 上
        // 是 CRLF，會讓訊息混用兩種行尾。固定 LF 讓輸出跨平台一致。
        var builder = new StringBuilder();
        builder.Append("以下是系統例外紀錄中的一筆例外（相同的例外已合併成一列並累計次數）。\n\n");

        foreach (var line in BuildDetailLines(item))
        {
            builder.Append(line.Key).Append('：').Append(Normalize(line.Value)).Append('\n');
        }

        builder.Append('\n');
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            builder.Append("完整堆疊：堆疊不可得（堆疊檔已被清除或當初寫檔失敗）。\n");
        }
        else
        {
            builder.Append(StackStartMarker).Append('\n')
                .Append(Normalize(stackTrace).TrimEnd('\n')).Append('\n')
                .Append(StackEndMarker).Append('\n');
        }

        return builder.ToString();
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
}
