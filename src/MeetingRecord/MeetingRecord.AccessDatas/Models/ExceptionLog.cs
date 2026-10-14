namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 系統例外紀錄：相同簽章的例外合併為一列並累加次數。
///
/// 與 <see cref="AuditLog"/> 的分工：AuditLog 記「誰對什麼做了什麼」的業務稽核軌跡，
/// 本表記「程式在哪裡拋出了什麼例外」。兩者刻意不合併。
///
/// 完整堆疊不存在本表，而是寫到檔案系統（見 <see cref="StackTraceFile"/>），
/// 本表只保留足以判讀的診斷摘要。
/// </summary>
public class ExceptionLog
{
    public int Id { get; set; }

    /// <summary>
    /// 合併鍵的 SHA-256（64 字元小寫十六進位）。唯一索引。
    /// 鍵＝例外類型 ＋ 訊息 ＋ 頁面 ＋ 操作。
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>例外型別全名，例如 System.NullReferenceException。</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>ex.Message，超長時截斷。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>來源：畫面／WebAPI／系統啟動／背景作業／系統／瀏覽器／未知。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>頁面或請求路徑（已去除查詢字串）。</summary>
    public string? Page { get; set; }

    /// <summary>
    /// 操作＝日誌訊息「樣板」，例如 Failed to create category. Name={CategoryName}。
    /// ⚠️ 必須是樣板而非算好的訊息，否則每個參數值都會變成一個新簽章。
    /// </summary>
    public string? Operation { get; set; }

    /// <summary>記錄器名稱，例如 MeetingRecord.Business.Services.DataAccess.CategoryService。</summary>
    public string? LoggerName { get; set; }

    /// <summary>首次遇到這個例外的使用者帳號。刻意不記姓名／Email（個資）。</summary>
    public string? Account { get; set; }

    public int? UserId { get; set; }

    /// <summary>堆疊檔案相對路徑，例如 202609/ab12cd34ef567890.txt。寫檔失敗時為 null。</summary>
    public string? StackTraceFile { get; set; }

    /// <summary>累計發生次數，從首次發生起累加，不重置。</summary>
    public long OccurrenceCount { get; set; }

    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }

    /// <summary>
    /// 最後一次發生時的錯誤追蹤碼。使用者回報的追蹤碼可在 /logs 找到該次操作的所有日誌行。
    /// 背景作業、啟動流程等沒有請求或互動的情況為 null。
    /// </summary>
    public string? LastTraceId { get; set; }
}
