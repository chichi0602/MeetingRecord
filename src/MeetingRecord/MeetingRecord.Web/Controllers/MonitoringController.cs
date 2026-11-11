using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 監控與診斷（WASM 遷移 M5，1.1.32 起逐頁加端點）。**只限管理者**（同畫面：這些頁的權限鍵刻意不在角色權限目錄裡）。
/// </summary>
[Route("api/v1/monitoring")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class MonitoringController(
    IDatabaseUsageService databaseUsageService,
    StorageUsageService storageUsageService) : ControllerBase
{
    /// <summary>資料庫用量（SQLite 檔案、頁面配置、各資料表）。失敗時報表本身帶 <c>errorMessage</c>，同畫面。</summary>
    [HttpGet("database-usage")]
    public async Task<ActionResult<ApiResult<DatabaseUsageReport>>> DatabaseUsage(CancellationToken cancellationToken)
        => Ok(ApiResult<DatabaseUsageReport>.SuccessResult(await databaseUsageService.GetReportAsync(cancellationToken)));

    /// <summary>
    /// 檔案儲存用量（外部目錄與磁碟可用空間）。影音目錄可能很大，所以和資料庫用量分開：畫面先顯示資料庫、再等這支。
    /// </summary>
    [HttpGet("storage-usage")]
    public async Task<ActionResult<ApiResult<StorageUsageReport>>> StorageUsage()
        => Ok(ApiResult<StorageUsageReport>.SuccessResult(await Task.Run(storageUsageService.GetReport)));
}
