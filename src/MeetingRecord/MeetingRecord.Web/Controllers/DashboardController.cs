using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Dashboards;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 儀表板（1.1.12）：登入即可看（0.4.97 起不檢查頁面權限），只放全公司的彙總數字、不含明細。
/// 呼叫者看不到的專案，名稱由 <see cref="DashboardService"/> 改成「其他專案」（API 呼叫時同樣以呼叫者的資料範圍判斷）。
/// </summary>
[Route("api/v1/dashboard")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class DashboardController(DashboardService dashboardService, ILogger<DashboardController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<DashboardSummary>>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(ApiResult<DashboardSummary>.SuccessResult(await dashboardService.GetSummaryAsync(cancellationToken), "取得儀表板成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading dashboard summary through API failed.");
            return this.ApiServerError<DashboardSummary>("取得儀表板失敗", ex);
        }
    }
}
