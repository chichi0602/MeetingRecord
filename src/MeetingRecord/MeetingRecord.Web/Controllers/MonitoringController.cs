using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Health;

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
    IAuditLogService auditLogService,
    ISystemHealthService systemHealthService,
    ILogQueryService logQueryService,
    AiConnectionTestService aiConnectionTestService,
    ILogger<MonitoringController> logger) : ControllerBase
{
    /// <summary>系統健康「最近 24 小時警告以上日誌」最多列幾筆（最新的）。更多請到「日誌檢視」。</summary>
    public const int RecentWarningTake = 200;

    /// <summary>
    /// 系統健康監控（1.1.35）：跑一輪全部檢查。每次呼叫都會跑，所以任何檢查都不可呼叫付費 AI（開發慣例 §6.3）。
    /// 失敗回 500，訊息同畫面「讀取系統健康狀態失敗：例外型別。」。
    /// </summary>
    [HttpGet("health")]
    public async Task<ActionResult<ApiResult<SystemHealthReport>>> Health(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(ApiResult<SystemHealthReport>.SuccessResult(await systemHealthService.GetReportAsync(cancellationToken)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Loading system health report through API failed.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult<SystemHealthReport>.FailureResult($"讀取系統健康狀態失敗：{ex.GetType().Name}。", StatusCodes.Status500InternalServerError));
        }
    }

    /// <summary>
    /// 系統健康「最近 24 小時警告以上日誌」（1.1.35）：最新 <see cref="RecentWarningTake"/> 筆，時間正序（舊→新），畫面自己反轉。
    /// 讀檔失敗時結果帶 <c>message</c>，同畫面。
    /// </summary>
    [HttpGet("logs/recent-warnings")]
    public async Task<ActionResult<ApiResult<LogQueryResult>>> RecentWarnings(CancellationToken cancellationToken)
    {
        try
        {
            var (start, end) = LogTimeRanges.Last(TimeSpan.FromHours(24), DateTime.Now);
            var result = await logQueryService.QueryAsync(new LogQueryRequest
            {
                StartTime = start,
                EndTime = end,
                MinimumLevel = LogLevelRank.Warn,
                Take = RecentWarningTake,
            }, cancellationToken);
            return Ok(ApiResult<LogQueryResult>.SuccessResult(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Loading recent warning logs for system health through API failed.");
            return Ok(ApiResult<LogQueryResult>.SuccessResult(new LogQueryResult { Message = $"讀取日誌失敗：{ex.GetType().Name}。" }));
        }
    }

    /// <summary>
    /// 測試 AI 連線（1.1.35）：實際呼叫一次文字生成與語音轉錄，**會產生費用**（確認框在畫面上）。
    /// 經 AiCallGateway，記入用量帳本、AI 對話紀錄與每月上限；稽核 <c>AiConnection.Test</c> 由伺服器寫。
    /// </summary>
    [HttpPost("ai-connection-test")]
    public async Task<ActionResult<ApiResult<List<AiConnectionProbeResult>>>> TestAiConnection(CancellationToken cancellationToken)
    {
        List<AiConnectionProbeResult> results;
        try
        {
            results = [.. await aiConnectionTestService.RunAsync(cancellationToken)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI connection test failed unexpectedly.");
            return Ok(ApiResult<List<AiConnectionProbeResult>>.SuccessResult([new AiConnectionProbeResult("測試", false, string.Empty, string.Empty, null, $"測試失敗：{ex.Message}")]));
        }

        var summary = string.Join("; ", results.Select(x => $"{x.Item}={(x.Success ? "OK" : "Fail")}"));
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.AiConnectionTest,
            success: results.All(x => x.Success),
            targetType: "LlmSettings",
            targetId: "*",
            detail: $"{summary}; via={AuditVia.Api}");
        return Ok(ApiResult<List<AiConnectionProbeResult>>.SuccessResult(results));
    }

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
