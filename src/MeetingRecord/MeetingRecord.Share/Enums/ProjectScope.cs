namespace MeetingRecord.Share.Enums;

/// <summary>
/// 「使用中｜已完成｜全部」檢視切換（1.0.46）。專案頁與待辦頁共用；每次開頁都回到 <see cref="Active"/>。
/// 「已完成」就是封存（<c>ProjectAdapterModel.ArchivedStatus</c>）。
/// </summary>
public enum ProjectScope
{
    /// <summary>使用中：未開始、進行中、暫緩、等待——除了已完成以外的。</summary>
    Active = 0,

    /// <summary>已完成（封存）。</summary>
    Archived = 1,

    /// <summary>全部。</summary>
    All = 2,
}
