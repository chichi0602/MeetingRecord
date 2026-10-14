using System.Security.Cryptography;
using System.Text;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 例外的合併鍵計算。
///
/// 相同簽章的例外會被併成同一列並累加次數 —— 大語言模型主機逾時這類問題往往在短時間內
/// 重複數百次，不合併的話會把少見而更值得注意的例外淹沒。
///
/// 合併鍵＝<b>例外類型 ＋ 訊息 ＋ 頁面 ＋ 操作</b>。把「頁面」與「操作」納入，是為了保留
/// 「在哪一頁、哪個動作出錯」的診斷價值：同一個 NullReferenceException 出現在專案頁與
/// 使用者頁，是兩個不同的問題。
/// </summary>
public static class ExceptionSignature
{
    /// <summary>資料庫欄位保留的訊息長度上限。超過就截斷，避免單筆過大。</summary>
    public const int MaxMessageLength = 1000;

    /// <summary>
    /// 列數達上限後，所有新例外共用的哨兵簽章。
    /// 用固定字串而非雜湊，讓它一眼可辨、也不可能與真實簽章相撞（真實簽章一律是 64 碼十六進位）。
    /// </summary>
    public const string OverflowSignature = "__OVERFLOW__";

    /// <summary>
    /// 計算簽章。四個組成以 \n 串接後取 SHA-256，回傳 64 字元小寫十六進位。
    /// </summary>
    /// <param name="operation">
    /// ⚠️ 必須是日誌訊息的<b>樣板</b>（<c>Failed to create category. Name={CategoryName}</c>），
    /// 不是算好的訊息。傳入算好的訊息會讓每個參數值變成一個新簽章，列數立刻失控。
    /// </param>
    public static string Compute(string exceptionType, string message, string? page, string? operation)
    {
        var raw = string.Join('\n',
            exceptionType ?? string.Empty,
            message ?? string.Empty,
            page ?? string.Empty,
            operation ?? string.Empty);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>把訊息截到資料庫欄位可接受的長度。</summary>
    public static string TruncateMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return message.Length <= MaxMessageLength
            ? message
            : message[..MaxMessageLength];
    }
}
