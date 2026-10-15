using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 稽核紀錄頁的查詢條件（1.0.32）。比照 <see cref="AiCallLogQuery"/>：自己的專用查詢型別。
/// ⚠️ 時間一律是<b>伺服器本地時間</b>；轉成 UTC 比對由 <c>AuditLogQueryService</c> 負責（資料庫存 UTC）。
/// </summary>
public class AuditLogQuery
{
    /// <summary>起始時間（含，本地時間）。</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>結束時間（含，本地時間）。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>動作代碼（完全相同）；空字串表示不限。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>結果；null 表示不限。</summary>
    public bool? Success { get; set; }

    /// <summary>操作者帳號（部分比對）。</summary>
    public string ActorAccount { get; set; } = string.Empty;

    /// <summary>來源 IP（前綴比對，例如 <c>192.168.</c>）。</summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>關鍵字：比對動作、操作者帳號、目標類型、目標識別、摘要與 IP（部分比對）。</summary>
    public string Keyword { get; set; } = string.Empty;

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    /// <summary>只有發生時間可排序；null 或 true 為新到舊。</summary>
    public bool? SortDescending { get; set; }
}
