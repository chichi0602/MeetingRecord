namespace MeetingRecord.Models.AdapterModel;

/// <summary>
/// 系統例外紀錄的畫面繫結模型。本頁唯讀，因此不需要 ICloneable
/// （Clone() 是為了「編輯前隔離，避免雙向繫結污染來源資料」而存在，這裡沒有編輯）。
/// </summary>
public class ExceptionLogAdapterModel
{
    public int Id { get; set; }

    public string Signature { get; set; } = string.Empty;

    public string ExceptionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? Page { get; set; }

    public string? Operation { get; set; }

    public string? LoggerName { get; set; }

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public string? StackTraceFile { get; set; }

    public long OccurrenceCount { get; set; }

    public DateTime FirstOccurredAt { get; set; }

    public DateTime LastOccurredAt { get; set; }

    /// <summary>最後一次發生時的錯誤追蹤碼。</summary>
    public string? LastTraceId { get; set; }

    /// <summary>型別全名太長，清單只顯示最後一節（例如 NullReferenceException）。</summary>
    public string ShortExceptionType
    {
        get
        {
            var index = ExceptionType.LastIndexOf('.');
            return index >= 0 && index < ExceptionType.Length - 1
                ? ExceptionType[(index + 1)..]
                : ExceptionType;
        }
    }
}
