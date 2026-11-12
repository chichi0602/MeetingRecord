using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Health;
using MeetingRecord.Web.Streaming;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 監控與診斷（1.1.32 起逐頁加端點）。**只限管理者**（同畫面：這些頁的權限鍵刻意不在角色權限目錄裡）。
/// 稽核由這裡寫（detail 加 <c>via=Api</c>）。
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
    DiagnosticAiService diagnosticAiService,
    IPdfRenderer pdfRenderer,
    IAuditActorProvider actorProvider,
    ILogger<MonitoringController> logger) : ControllerBase
{
    /// <summary>日誌檢視稽核的目標型別（同畫面）。</summary>
    private const string LogQueryTargetType = "LogQuery";

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

    /// <summary>日誌檢視頁的設定（1.1.42）：AI 日誌分析能不能用。</summary>
    [HttpGet("logs/meta")]
    public ActionResult<ApiResult<LogViewerMetaDto>> LogViewerMeta()
    {
        var available = diagnosticAiService.IsAvailable;
        return Ok(ApiResult<LogViewerMetaDto>.SuccessResult(new LogViewerMetaDto
        {
            AiAvailable = available,
            AiUnavailableReason = available ? string.Empty : diagnosticAiService.UnavailableReason,
        }));
    }

    /// <summary>
    /// 日誌檢視查詢（1.1.42）：讀 NLog 的檔案目標，回時間正序（舊→新），畫面自己反轉。
    /// 條件被夾住時（區間過長、筆數過多）<c>appliedStartTime</c>／<c>appliedEndTime</c> 是實際查詢的區間；讀檔失敗時結果帶 <c>message</c>，同畫面。
    /// </summary>
    [HttpPost("logs/query")]
    public async Task<ActionResult<ApiResult<LogQueryResult>>> QueryLogs([FromBody] LogQueryRequest request, CancellationToken cancellationToken)
        => Ok(ApiResult<LogQueryResult>.SuccessResult(await RunLogQueryAsync(request, cancellationToken)));

    /// <summary>
    /// 匯出 .log（1.1.42）：伺服器依同一組條件重查、組成原文（時間正序，UTF-8 含 BOM），內容與畫面「複製」相同。
    /// 沒有日誌回 400。稽核 <c>LogViewer.Export</c> 由伺服器寫。
    /// </summary>
    [HttpPost("logs/export")]
    public async Task<IActionResult> ExportLogs([FromBody] LogQueryRequest request, CancellationToken cancellationToken)
    {
        var result = await RunLogQueryAsync(request, cancellationToken);
        if (result.Entries.Count == 0)
        {
            return BadRequest(ApiResult.FailureResult(string.IsNullOrWhiteSpace(result.Message) ? "沒有日誌可以匯出。" : result.Message));
        }

        var bytes = Utf8WithBom(string.Join(Environment.NewLine, result.Entries.Select(entry => entry.Raw)));
        logger.LogInformation("Log export downloaded through API. Rows={Rows}", result.Entries.Count);
        await WriteLogAuditAsync(AuditActions.LogViewerExport, success: true, result, $"format=log; rows={result.Entries.Count}");
        return File(bytes, "text/plain", $"MeetingRecord.Web-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>
    /// AI 日誌分析（1.1.42，會產生費用；確認窗在前端）。伺服器依同一組條件**重查**、取最新
    /// <see cref="AiLogPromptBuilder.MaxEntries"/> 筆組成送出內容，不收前端給的日誌。回應是串流（見 <see cref="DiagnosticAiStreamEventDto"/>）；
    /// 前端關掉視窗＝中斷請求，AI 呼叫跟著停（帳本記成 Cancelled）。AI 不能用、沒有日誌、第一段文字前就失敗回一般的 400。
    /// 成功、失敗、取消都寫稽核（同畫面）。
    /// </summary>
    [HttpPost("logs/ai-analysis")]
    public async Task AnalyzeLogs([FromBody] LogQueryRequest request, CancellationToken cancellationToken)
    {
        if (!diagnosticAiService.IsAvailable)
        {
            await new BadRequestObjectResult(ApiResult.FailureResult($"AI 分析目前無法使用：{diagnosticAiService.UnavailableReason}"))
                .ExecuteResultAsync(ControllerContext);
            return;
        }

        var result = await RunLogQueryAsync(request, cancellationToken);
        var prompt = AiLogPromptBuilder.Build(result.Entries);
        if (prompt.IsEmpty)
        {
            await new BadRequestObjectResult(ApiResult.FailureResult("沒有日誌可以分析，請調整查詢條件。")).ExecuteResultAsync(ControllerContext);
            return;
        }

        var (start, end) = (result.AppliedStartTime, result.AppliedEndTime);
        var outcome = await NdjsonStreamer.RunAsync(
            this,
            onDelta => diagnosticAiService.AnalyzeAsync(
                AiUsageFeature.LogAnalysis,
                AiDiagnosticPrompts.LogAnalysis,
                prompt.UserMessage,
                onDelta,
                cancellationToken,
                relatedInfo: $"日誌 {start:yyyy-MM-dd HH:mm}～{end:yyyy-MM-dd HH:mm}，送出 {prompt.IncludedEntryCount}／{prompt.TotalEntryCount} 筆"),
            delta => new DiagnosticAiStreamEventDto { Type = DiagnosticAiStreamEventDto.Delta, Text = delta },
            analysis => new DiagnosticAiStreamEventDto
            {
                Type = DiagnosticAiStreamEventDto.Done,
                Content = analysis.Content,
                Model = analysis.ModelName,
                Meta = BuildLogAiMeta(request, result, prompt, analysis),
            },
            error => new BadRequestObjectResult(ApiResult.FailureResult(LogAiFailure.Describe(LogAiAnalysisFailure(error, prompt, cancellationToken)))),
            error => new DiagnosticAiStreamEventDto { Type = DiagnosticAiStreamEventDto.Error, Message = LogAiFailure.Describe(LogAiAnalysisFailure(error, prompt, cancellationToken)) },
            cancellationToken);

        // 同畫面：成功記筆數、字數、模型、耗時；使用者關窗記「使用者取消」；失敗記例外型別。斷線也要記得到，所以不傳請求的取消權杖。
        var detail = outcome switch
        {
            { Error: null, Result: { } analysis } => BuildLogAiAuditDetail(prompt, analysis.ModelName, analysis.Elapsed),
            { Error: OperationCanceledException } when cancellationToken.IsCancellationRequested => BuildLogAiAuditDetail(prompt, null, null) + "；使用者取消",
            { Error: { } error } => BuildLogAiAuditDetail(prompt, null, null) + $"；失敗：{error.GetType().Name}",
            _ => BuildLogAiAuditDetail(prompt, null, null),
        };
        await WriteLogAuditAsync(AuditActions.LogViewerAiAnalyze, success: outcome.Error is null, result, detail);
    }

    /// <summary>
    /// 匯出 AI 日誌分析 PDF（1.1.42）：結果與說明行由前端帶（剛收到的），附錄「實際送出的日誌」由伺服器依同一組條件重查重組，
    /// 操作者是 API 呼叫者。稽核由伺服器寫。
    /// </summary>
    [HttpPost("logs/ai-analysis/pdf")]
    public async Task<IActionResult> ExportLogAiPdf([FromBody] LogAiPdfRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Markdown))
        {
            return BadRequest(ApiResult.FailureResult("沒有分析結果可以匯出。"));
        }

        try
        {
            var result = await RunLogQueryAsync(request.Query, cancellationToken);
            var prompt = AiLogPromptBuilder.Build(result.Entries);
            var exportedAt = DateTime.Now;
            var html = DiagnosticReportExporter.BuildHtml(
                "AI 日誌分析報告",
                [.. request.Meta.Select(x => (x.Label, x.Value)), ("操作者", actorProvider.GetActor().Account ?? string.Empty)],
                request.Markdown,
                "附錄：實際送出的日誌",
                prompt.UserMessage,
                exportedAt);

            var pdf = await pdfRenderer.RenderAsync(html, cancellationToken);
            await WriteLogAuditAsync(AuditActions.LogViewerAiAnalyzeExportPdf, success: true, result, $"rows={prompt.IncludedEntryCount}; bytes={pdf.Length}");
            return File(pdf, "application/pdf", DiagnosticReportExporter.BuildFileName("AI日誌分析", exportedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI log analysis PDF export through API failed.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult.FailureResult($"匯出 PDF 失敗：{ex.Message}", StatusCodes.Status500InternalServerError));
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

    /// <summary>查詢日誌；讀檔失敗時回帶訊息的空結果（同畫面「查詢日誌失敗：例外型別。」）。</summary>
    private async Task<LogQueryResult> RunLogQueryAsync(LogQueryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await logQueryService.QueryAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Log query through API failed.");
            return new LogQueryResult
            {
                Message = $"查詢日誌失敗：{ex.GetType().Name}。",
                AppliedStartTime = request.StartTime,
                AppliedEndTime = request.EndTime,
            };
        }
    }

    /// <summary>AI 日誌分析結果上方的說明行（同畫面）。</summary>
    private static List<DiagnosticAiMetaLineDto> BuildLogAiMeta(LogQueryRequest request, LogQueryResult result, AiPromptBuildResult prompt, DiagnosticAiResult analysis)
    {
        var rows = new List<DiagnosticAiMetaLineDto>
        {
            new() { Label = "查詢區間", Value = $"{result.AppliedStartTime:yyyy-MM-dd HH:mm:ss} ～ {result.AppliedEndTime:yyyy-MM-dd HH:mm:ss}" },
            new() { Label = "最低等級", Value = request.MinimumLevel == LogLevelRank.Any ? "不限" : LogLevelRankHelper.ToLevelText(request.MinimumLevel) },
        };

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            rows.Add(new() { Label = "關鍵字", Value = request.Keyword });
        }

        if (!string.IsNullOrWhiteSpace(request.TraceId))
        {
            rows.Add(new() { Label = "TraceId", Value = request.TraceId });
        }

        rows.Add(new()
        {
            Label = "分析範圍",
            Value = $"分析 {prompt.IncludedEntryCount} 筆／查詢 {prompt.TotalEntryCount} 筆" + (prompt.DroppedByEntryLimit ? "（只取最新的部分）" : string.Empty),
        });
        rows.Add(new() { Label = "使用模型", Value = analysis.ModelName });
        if (analysis.Usage is { } usage)
        {
            rows.Add(new() { Label = "用量", Value = $"輸入 {usage.InputTokens:N0}　輸出 {usage.OutputTokens:N0} tokens" });
        }

        rows.Add(new() { Label = "耗時", Value = $"{analysis.Elapsed.TotalSeconds:F1} 秒" });
        return rows;
    }

    /// <summary>同畫面：額度用完記 warning、使用者關窗只記 information、其他記 error。</summary>
    private Exception LogAiAnalysisFailure(Exception error, AiPromptBuildResult prompt, CancellationToken cancellationToken)
    {
        if (error is AiQuotaExceededException)
        {
            logger.LogWarning("AI log analysis blocked by AI quota.");
        }
        else if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("AI log analysis cancelled by user.");
        }
        else
        {
            logger.LogError(error, "AI log analysis failed. Entries={Entries}, Characters={Characters}", prompt.IncludedEntryCount, prompt.CharacterCount);
        }

        return error;
    }

    /// <summary>稽核只記筆數、字數與模型，不記日誌內容（日誌本身就可能含敏感資訊），同畫面。</summary>
    private static string BuildLogAiAuditDetail(AiPromptBuildResult prompt, string? modelName, TimeSpan? elapsed)
        => $"送出 {prompt.IncludedEntryCount}/{prompt.TotalEntryCount} 筆、{prompt.CharacterCount} 字元"
            + (modelName is null ? string.Empty : $"；模型 {modelName}")
            + (elapsed is null ? string.Empty : $"；耗時 {(long)elapsed.Value.TotalMilliseconds} ms");

    /// <summary>日誌檢視的稽核：目標是實際查詢的區間（同畫面），另加 via=Api。</summary>
    private Task WriteLogAuditAsync(string action, bool success, LogQueryResult result, string detail)
        => auditLogService.WriteForCurrentUserAsync(
            action,
            success: success,
            targetType: LogQueryTargetType,
            targetId: $"{result.AppliedStartTime:yyyyMMddHHmmss}-{result.AppliedEndTime:yyyyMMddHHmmss}",
            detail: $"{detail}; via={AuditVia.Api}");

    /// <summary>UTF-8 含 BOM：Windows 記事本與 Excel 開中文才不會變亂碼。</summary>
    private static byte[] Utf8WithBom(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    /// <summary>稽核內容同畫面（1.1.33 前由畫面寫），另加 via=Api。寫入失敗不推翻「等級已經套用」這個事實。</summary>
    private Task WriteLogLevelAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
        => auditLogService.WriteForCurrentUserAsync(
            action,
            success: true,
            targetType: "NLog",
            targetId: "logger:*",
            detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。; via={AuditVia.Api}");
}
