using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Models.AdapterModel;

/// <summary>
/// 稽核紀錄頁的畫面繫結模型（1.0.32）。本頁唯讀，不需要 ICloneable。
/// ⚠️ <see cref="OccurredAt"/> 已由查詢服務轉成<b>伺服器本地時間</b>；資料庫存的是 UTC（<see cref="OccurredAtUtc"/>）。
/// </summary>
public class AuditLogAdapterModel
{
    public int Id { get; set; }

    /// <summary>發生時間（本地）。</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>發生時間（UTC，資料庫原值）。</summary>
    public DateTime OccurredAtUtc { get; set; }

    public int? ActorUserId { get; set; }

    public string? ActorAccount { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string? Detail { get; set; }

    public bool Success { get; set; }

    public string? ClientIp { get; set; }

    /// <summary>動作的中文名稱；對照表沒有就顯示代碼。</summary>
    public string ActionDisplayName => AuditActions.Describe(Action);

    /// <summary>動作類別（代碼第一個 <c>.</c> 之前），畫面依此上色。</summary>
    public string ActionCategory => AuditActions.CategoryOf(Action);

    public string SuccessText => Success ? "成功" : "失敗";

    /// <summary>操作者：<c>帳號（Id=N）</c>；系統排程或匿名顯示「（系統／匿名）」。</summary>
    public string ActorDisplay => (ActorAccount, ActorUserId) switch
    {
        ({ Length: > 0 } account, { } id) => $"{account}（Id={id}）",
        ({ Length: > 0 } account, null) => account,
        (_, { } id) => $"Id={id}",
        _ => "（系統／匿名）",
    };

    /// <summary>目標：<c>類型#識別</c>；都沒有時顯示「—」。</summary>
    public string TargetDisplay => (TargetType, TargetId) switch
    {
        ({ Length: > 0 } type, { Length: > 0 } id) => $"{type}#{id}",
        ({ Length: > 0 } type, _) => type,
        (_, { Length: > 0 } id) => id,
        _ => "—",
    };
}
