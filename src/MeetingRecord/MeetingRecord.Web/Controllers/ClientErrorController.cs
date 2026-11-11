using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 瀏覽器端錯誤回報（WASM 遷移 M6，1.1.47）：WASM 頁的 <c>client-error-reporter.js</c> 捕捉後送到這裡，
/// 記進日誌檔與系統例外紀錄（來源「瀏覽器」）。以前經 Blazor Server 的 circuit 回報，WASM 頁的錯誤送不出去。
///
/// <para>任何登入者都能回報自己頁面上的錯誤；匿名者 401，無從灌資料。依帳號限流（<see cref="BrowserErrorReporter"/>）。</para>
/// </summary>
[Route("api/v1/client-errors")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class ClientErrorController(BrowserErrorReporter reporter, IAuditActorProvider actorProvider) : ControllerBase
{
    /// <summary>記錄一筆；回傳是否記下（停用或超過上限時是 false，前端不必處理）。</summary>
    [HttpPost]
    public ActionResult<ApiResult<bool>> Report([FromBody] ClientErrorReportDto report)
    {
        var (userId, account) = actorProvider.GetActor();
        var recorded = reporter.Report(userId ?? 0, account, report.Kind, report.Message, report.Stack, report.Source, report.Path);
        return Ok(ApiResult<bool>.SuccessResult(recorded));
    }
}
