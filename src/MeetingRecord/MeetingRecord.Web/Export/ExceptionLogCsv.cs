using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Web.Export;

/// <summary>
/// 系統例外 CSV 的欄位（0.4.122；1.1.40 從 <c>ExceptionLogView.CsvColumns</c> 搬來，匯出改由伺服器產生）。
/// 含帳號：這份是管理者自己下載的，不會送給 AI。
/// </summary>
public static class ExceptionLogCsv
{
    public static readonly IReadOnlyList<CsvColumn<ExceptionLogAdapterModel>> Columns =
    [
        new("最後發生", x => x.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("次數", x => x.OccurrenceCount.ToString()),
        new("例外類型", x => x.ExceptionType),
        new("訊息", x => x.Message),
        new("來源", x => x.Source),
        new("頁面", x => x.Page),
        new("操作", x => x.Operation),
        new("記錄器", x => x.LoggerName),
        new("使用者", x => x.Account),
        new("首次發生", x => x.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("最後追蹤碼", x => x.LastTraceId),
    ];
}
