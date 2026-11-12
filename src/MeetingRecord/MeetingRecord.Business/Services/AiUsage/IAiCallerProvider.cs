namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// AI 呼叫是誰觸發的（用量帳本、每人額度、AI 對話紀錄、問答訊息的作者）。1.1.28。
///
/// <para>
/// 實作在 Web 層（<c>AiCallerProvider</c>）：背景工作看自己填入上傳者的 <see cref="Other.CurrentUserService"/>；
/// API 請求時 CurrentUserService 是空的，改從 HttpContext 的 claims 取。服務層若只看 CurrentUserService，
/// 頁面經 API 問 AI、抽待辦、產生會議紀錄都記不到人，每人額度也就擋不住。
/// </para>
///
/// <para>取不到時回 <c>(null, null)</c>，與 <see cref="AiUsageAttribution.Resolve(Models.Others.CurrentUser?)"/> 相同。</para>
/// </summary>
public interface IAiCallerProvider
{
    (int? UserId, string? UserName) GetCaller();
}
