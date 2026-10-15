namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 稽核軌跡寫入服務；寫入失敗不應中斷主要業務流程。
/// </summary>
public interface IAuditLogService
{
    Task WriteAsync(
        string action,
        bool success = true,
        int? actorUserId = null,
        string? actorAccount = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null);

    /// <summary>
    /// 以「目前操作者」寫一筆稽核（1.0.34，業務資料事件用）。操作者由 <see cref="IAuditActorProvider"/> 決定：
    /// 網頁與 API 兩條路都認得出是誰，呼叫端不用自己傳。
    /// </summary>
    Task WriteForCurrentUserAsync(
        string action,
        bool success = true,
        string? targetType = null,
        string? targetId = null,
        string? detail = null);
}
