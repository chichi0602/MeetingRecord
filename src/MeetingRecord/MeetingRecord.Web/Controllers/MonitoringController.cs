using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 監控與診斷（WASM 遷移 M5，1.1.32 起逐頁加端點）。**只限管理者**（同畫面：這些頁的權限鍵刻意不在角色權限目錄裡）。
/// 畫面以前自己寫的稽核改由這裡寫（動作代碼不變，detail 加 <c>via=Api</c>）。
/// </summary>
[Route("api/v1/monitoring")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class MonitoringController(
    IDatabaseUsageService databaseUsageService,
    StorageUsageService storageUsageService,
    LogLevelRuntimeState logLevelState,
    IAuditLogService auditLogService) : ControllerBase
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

    /// <summary>日誌等級現況（1.1.33）。每次都讀真實狀態，不快取。</summary>
    [HttpGet("log-level")]
    public ActionResult<ApiResult<LogLevelStateDto>> LogLevel()
        => Ok(ApiResult<LogLevelStateDto>.SuccessResult(ReadLogLevelState()));

    /// <summary>
    /// 套用日誌等級（1.1.33）：只改記憶體中 NLog 的 <c>&lt;logger name="*"&gt;</c> 規則，重新啟動就回到 nlog.config 的值。
    /// 只接受 Trace～Fatal（不收「不限」與「無法解析」）。調到 Trace／Debug 的確認在畫面上。
    /// </summary>
    [HttpPut("log-level")]
    public async Task<ActionResult<ApiResult<LogLevelStateDto>>> ApplyLogLevel([FromBody] LogLevelApplyRequestDto request)
    {
        if (!LogLevelRankHelper.IsRealLevel(request.Level))
        {
            return BadRequest(ApiResult<LogLevelStateDto>.FailureResult("日誌等級不正確，只能是 TRACE、DEBUG、INFO、WARN、ERROR、FATAL 其中之一。"));
        }

        var previous = logLevelState.EffectiveLevel;
        if (!logLevelState.Apply(request.Level))
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult<LogLevelStateDto>.FailureResult("套用失敗，請確認 NLog 設定。", StatusCodes.Status500InternalServerError));
        }

        await WriteLogLevelAuditAsync(AuditActions.LogLevelApply, previous, request.Level);
        return Ok(ApiResult<LogLevelStateDto>.SuccessResult(ReadLogLevelState()));
    }

    /// <summary>還原為系統預設等級（1.1.33）：拿掉執行期覆寫，回到 nlog.config 的值。</summary>
    [HttpPost("log-level/restore")]
    public async Task<ActionResult<ApiResult<LogLevelStateDto>>> RestoreLogLevel()
    {
        var previous = logLevelState.EffectiveLevel;
        if (!logLevelState.RestoreDefault())
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult<LogLevelStateDto>.FailureResult("還原失敗，請確認 NLog 設定。", StatusCodes.Status500InternalServerError));
        }

        await WriteLogLevelAuditAsync(AuditActions.LogLevelRestore, previous, logLevelState.EffectiveLevel);
        return Ok(ApiResult<LogLevelStateDto>.SuccessResult(ReadLogLevelState()));
    }

    private LogLevelStateDto ReadLogLevelState() => new()
    {
        IsAvailable = logLevelState.IsAvailable,
        EffectiveLevel = logLevelState.EffectiveLevel,
        SystemDefaultLevel = logLevelState.SystemDefaultLevel,
        HasOverride = logLevelState.OverrideLevel is not null,
    };

    /// <summary>稽核內容同畫面（1.1.33 前由畫面寫），另加 via=Api。寫入失敗不推翻「等級已經套用」這個事實。</summary>
    private Task WriteLogLevelAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
        => auditLogService.WriteForCurrentUserAsync(
            action,
            success: true,
            targetType: "NLog",
            targetId: "logger:*",
            detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。; via={AuditVia.Api}");
}
