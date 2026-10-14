using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 系統例外紀錄的查詢條件。
///
/// 刻意不重用共用的 <see cref="DataRequest"/>：那支是 CRUD 清單頁共用的，
/// 加入時間範圍／來源／帳號只會讓每個頁面都背著用不到的欄位。
/// 作法比照「日誌檢視」的 LogQueryRequest（也是自己的專用查詢型別）。
/// </summary>
public class ExceptionLogQuery
{
    /// <summary>起始時間，比對 LastOccurredAt。</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>結束時間，比對 LastOccurredAt。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>來源，空字串表示不限。見 <see cref="ExceptionSources"/>。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>使用者帳號，部分比對。</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>關鍵字，同時比對 例外類型／訊息／頁面／操作。</summary>
    public string Keyword { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}
