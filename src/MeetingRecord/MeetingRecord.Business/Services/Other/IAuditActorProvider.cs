namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 找出「目前是誰在操作」給稽核用（1.0.34）。實作在 Web 層（<c>AuditActorProvider</c>）：
/// 背景工作看自己填入上傳者的 <see cref="CurrentUserService"/>；API 請求時它是空的，改從 HttpContext 的 claims 取。
/// 取不到時回傳 <c>(null, null)</c>。
/// </summary>
public interface IAuditActorProvider
{
    (int? UserId, string? Account) GetActor();
}
