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
}
