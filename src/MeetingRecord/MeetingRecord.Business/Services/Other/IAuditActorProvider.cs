namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 找出「目前是誰在操作」給稽核用（1.0.34）。實作在 Web 層（<c>AuditActorProvider</c>）：
/// Blazor 用 <see cref="CurrentUserService"/>，API 請求從 HttpContext 的 claims 取（CurrentUserService 在 API 是空的）。
/// 取不到時回傳 <c>(null, null)</c>。
/// </summary>
public interface IAuditActorProvider
{
    (int? UserId, string? Account) GetActor();
}
