using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Streaming;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 系統例外紀錄（WASM 遷移 M5，1.1.40）：<c>api/v1/exception-logs</c>，**只限管理者**（同畫面）。
///
/// <para>
/// 清單、堆疊、CSV、刪除與清除；稽核由伺服器寫（目標 <c>ExceptionLog</c>，內容同畫面，另加 <c>via=Api</c>），不記例外內容。
/// </para>
///
/// <para>
/// **AI 例外分析**會花錢、要邊產生邊顯示：回應是串流（一行一筆 JSON，見 <see cref="DiagnosticAiStreamEventDto"/>）。
/// 送出的內容由伺服器依例外編號自己組（<see cref="AiExceptionPromptBuilder"/>，不含帳號），不收前端的值。
/// 前端關掉視窗＝中斷請求：請求的取消權杖傳進服務層，AI 呼叫跟著停（帳本記成 Cancelled）。
/// </para>
/// </summary>
[Route("api/v1/exception-logs")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class ExceptionLogController(
    ExceptionLogService exceptionLogService,
    DiagnosticAiService diagnosticAiService,
    IOptionsMonitor<LogRetentionSettings> retentionOptions,
    IPdfRenderer pdfRenderer,
    IAuditLogService auditLogService,
    IAuditActorProvider actorProvider,
    ILogger<ExceptionLogController> logger) : ControllerBase
{
    private const string TargetType = "ExceptionLog";

    /// <summary>頁面要顯示的設定：保留天數、手動清除天數、上限、AI 能不能用。</summary>
    [HttpGet("meta")]
    public ActionResult<ApiResult<ExceptionLogMetaDto>> Meta()
    {
        var available = diagnosticAiService.IsAvailable;
        return Ok(ApiResult<ExceptionLogMetaDto>.SuccessResult(new ExceptionLogMetaDto
        {
            RetentionDays = retentionOptions.CurrentValue.ExceptionLogDays,
            PurgeDays = retentionOptions.CurrentValue.ManualExceptionLogDays,
            MaxRows = ExceptionLogService.MaxRows,
            AiAvailable = available,
            AiUnavailableReason = available ? string.Empty : diagnosticAiService.UnavailableReason,
        }));
    }

    /// <summary>清單一頁（「複製全部」用同一支、頁大小帶上限）。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<ExceptionLogAdapterModel>>>> Search([FromBody] ExceptionLogQuery query)
    {
        query.PageSize = Math.Clamp(query.PageSize, 1, ExceptionLogService.MaxRows);
        query.CurrentPage = Math.Max(1, query.CurrentPage);
        var result = await exceptionLogService.GetAsync(query);
        return Ok(ApiResult<PagedResult<ExceptionLogAdapterModel>>.SuccessResult(new PagedResult<ExceptionLogAdapterModel>
        {
            Items = [.. result.Result],
            TotalCount = result.Count,
            PageIndex = query.CurrentPage,
            PageSize = query.PageSize,
            TotalPages = (result.Count + query.PageSize - 1) / query.PageSize,
        }));
    }

    /// <summary>一列的堆疊全文（檔案不存在時是 null）與 AI 分析的送出字數；這一列不存在 404。</summary>
    [HttpGet("{id:int}/stack-trace")]
    public async Task<ActionResult<ApiResult<ExceptionLogStackTraceDto>>> StackTrace(int id)
    {
        if (await exceptionLogService.GetByIdAsync(id) is not { } item)
        {
            return NotFound(ApiResult<ExceptionLogStackTraceDto>.NotFoundResult("找不到這筆例外紀錄，可能已被刪除。"));
        }

        var stackTrace = NullIfBlank(await exceptionLogService.GetStackTraceAsync(id));
        return Ok(ApiResult<ExceptionLogStackTraceDto>.SuccessResult(new ExceptionLogStackTraceDto
        {
            StackTrace = stackTrace,
            AiPromptChars = AiExceptionPromptBuilder.Build(item, stackTrace).Length,
        }));
    }

    /// <summary>一次讀多列的堆疊（「複製全部」用，最多 <see cref="ExceptionLogService.MaxRows"/> 列）；找不到的值是 null。</summary>
    [HttpPost("stack-traces")]
    public async Task<ActionResult<ApiResult<Dictionary<int, string?>>>> StackTraces([FromBody] ExceptionLogStackTracesRequestDto request)
    {
        var ids = request.Ids.Distinct().ToList();
        if (ids.Count > ExceptionLogService.MaxRows)
        {
            return BadRequest(ApiResult<Dictionary<int, string?>>.FailureResult($"一次最多 {ExceptionLogService.MaxRows:N0} 列。"));
        }

        return Ok(ApiResult<Dictionary<int, string?>>.SuccessResult(await exceptionLogService.GetStackTracesAsync(ids)));
    }

    /// <summary>匯出 CSV（目前條件、不分頁，最多 <see cref="ExceptionLogService.MaxRows"/> 列）。寫稽核。</summary>
    [HttpPost("export/csv")]
    public async Task<IActionResult> ExportCsv([FromBody] ExceptionLogQuery query)
    {
        query.CurrentPage = 1;
        query.PageSize = ExceptionLogService.MaxRows;
        var result = await exceptionLogService.GetAsync(query);
        var bytes = CsvExport.ToCsv(ExceptionLogCsv.Columns, result.Result);
        await AuditAsync(AuditActions.ExceptionLogExport, "*", $"format=csv; rows={result.Count}");
        return File(bytes, CsvExport.ContentType, $"MeetingRecord.Web-exceptions-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    /// <summary>刪除一列（堆疊檔一併刪除；批次刪除由畫面逐筆呼叫）。不存在 404。寫稽核。</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(int id)
    {
        if (await exceptionLogService.GetByIdAsync(id) is not { } item)
        {
            return NotFound(ApiResult<object>.NotFoundResult("找不到要刪除的例外紀錄。"));
        }

        var result = await exceptionLogService.DeleteAsync(id);
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await AuditAsync(AuditActions.ExceptionLogDelete, id.ToString(), $"type={item.ShortExceptionType}");
        return Ok(ApiResult<object>.SuccessResult(null!, "刪除成功"));
    }

    /// <summary>清除「最後發生」早於 N 天前的紀錄（N 由伺服器設定決定，同畫面）。寫稽核。</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<ApiResult<object>>> Purge()
    {
        var days = retentionOptions.CurrentValue.ManualExceptionLogDays;
        var result = await exceptionLogService.PurgeAsync(days);
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await AuditAsync(AuditActions.ExceptionLogPurge, "*", $"days={days}; {result.Message}");
        return Ok(ApiResult<object>.SuccessResult(null!, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message));
    }

    /// <summary>清空全部紀錄與整個堆疊檔目錄。寫稽核。</summary>
    [HttpPost("clear-all")]
    public async Task<ActionResult<ApiResult<object>>> ClearAll()
    {
        var result = await exceptionLogService.ClearAllAsync();
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await AuditAsync(AuditActions.ExceptionLogClearAll, "*", "清空全部例外紀錄");
        return Ok(ApiResult<object>.SuccessResult(null!, "已清空全部例外紀錄"));
    }

    /// <summary>
    /// AI 例外分析（會產生費用；確認窗在前端）。回應是串流；這一列不存在 404，AI 不能用或第一段文字前就失敗回一般的 400。
    /// 成功、失敗、取消都寫稽核（同畫面）。
    /// </summary>
    [HttpPost("{id:int}/ai-analysis")]
    public async Task AiAnalysis(int id, CancellationToken cancellationToken)
    {
        if (await exceptionLogService.GetByIdAsync(id) is not { } item)
        {
            await new NotFoundObjectResult(ApiResult.NotFoundResult("找不到這筆例外紀錄，可能已被刪除。")).ExecuteResultAsync(ControllerContext);
            return;
        }

        if (!diagnosticAiService.IsAvailable)
        {
            await new BadRequestObjectResult(ApiResult.FailureResult($"AI 分析目前無法使用：{diagnosticAiService.UnavailableReason}"))
                .ExecuteResultAsync(ControllerContext);
            return;
        }

        var prompt = await BuildPromptAsync(item);
        var outcome = await NdjsonStreamer.RunAsync(
            this,
            onDelta => diagnosticAiService.AnalyzeAsync(
                AiUsageFeature.ExceptionAnalysis,
                AiDiagnosticPrompts.ExceptionAnalysis,
                prompt,
                onDelta,
                cancellationToken,
                relatedInfo: $"例外紀錄 #{item.Id}（{item.ExceptionType}）"),
            delta => new DiagnosticAiStreamEventDto { Type = DiagnosticAiStreamEventDto.Delta, Text = delta },
            result => new DiagnosticAiStreamEventDto
            {
                Type = DiagnosticAiStreamEventDto.Done,
                Content = result.Content,
                Model = result.ModelName,
                Meta = BuildMeta(item, result),
            },
            error => new BadRequestObjectResult(ApiResult.FailureResult(ExceptionAiFailure.Describe(LogFailure(error, id, cancellationToken)))),
            error => new DiagnosticAiStreamEventDto { Type = DiagnosticAiStreamEventDto.Error, Message = ExceptionAiFailure.Describe(LogFailure(error, id, cancellationToken)) },
            cancellationToken);

        // 同畫面：成功記字數、模型、耗時；使用者關窗記「使用者取消」；失敗記例外型別。斷線也要記得到，所以不傳請求的取消權杖。
        var detail = outcome switch
        {
            { Error: null, Result: { } result } => $"chars={prompt.Length}; model={result.ModelName}; elapsedMs={(long)result.Elapsed.TotalMilliseconds}",
            { Error: OperationCanceledException } when cancellationToken.IsCancellationRequested => $"chars={prompt.Length}; 使用者取消",
            { Error: { } error } => $"chars={prompt.Length}; 失敗：{error.GetType().Name}",
            _ => $"chars={prompt.Length}",
        };
        await AuditAsync(AuditActions.ExceptionLogAiAnalyze, id.ToString(), detail, success: outcome.Error is null);
    }

    /// <summary>
    /// 匯出 AI 例外分析 PDF：結果與說明行由前端帶（剛收到的），附錄「實際送出的例外內容」由伺服器依例外編號重組，
    /// 操作者是 API 呼叫者。這一列不存在 404。寫稽核。
    /// </summary>
    [HttpPost("{id:int}/ai-analysis/pdf")]
    public async Task<IActionResult> ExportAiPdf(int id, [FromBody] DiagnosticAiPdfRequestDto request, CancellationToken cancellationToken)
    {
        if (await exceptionLogService.GetByIdAsync(id) is not { } item)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這筆例外紀錄，可能已被刪除。"));
        }

        if (string.IsNullOrWhiteSpace(request.Markdown))
        {
            return BadRequest(ApiResult.FailureResult("沒有分析結果可以匯出。"));
        }

        try
        {
            var exportedAt = DateTime.Now;
            var html = DiagnosticReportExporter.BuildHtml(
                "AI 例外分析報告",
                [.. request.Meta.Select(x => (x.Label, x.Value)), ("操作者", actorProvider.GetActor().Account ?? string.Empty)],
                request.Markdown,
                "附錄：實際送出的例外內容",
                await BuildPromptAsync(item),
                exportedAt);

            var pdf = await pdfRenderer.RenderAsync(html, cancellationToken);
            await AuditAsync(AuditActions.ExceptionLogAiAnalyzeExportPdf, id.ToString(), $"bytes={pdf.Length}");
            return File(pdf, "application/pdf", DiagnosticReportExporter.BuildFileName("AI例外分析", exportedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI exception analysis PDF export through API failed. ExceptionLogId={ExceptionLogId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult.FailureResult($"匯出 PDF 失敗：{ex.Message}", StatusCodes.Status500InternalServerError));
        }
    }

    private async Task<string> BuildPromptAsync(ExceptionLogAdapterModel item)
        => AiExceptionPromptBuilder.Build(item, NullIfBlank(await exceptionLogService.GetStackTraceAsync(item.Id)));

    /// <summary>結果上方的說明行（同畫面）。</summary>
    private static List<DiagnosticAiMetaLineDto> BuildMeta(ExceptionLogAdapterModel item, DiagnosticAiResult result)
    {
        var meta = new List<DiagnosticAiMetaLineDto>
        {
            new() { Label = "例外類型", Value = item.ExceptionType },
            new() { Label = "累計次數", Value = item.OccurrenceCount.ToString("N0") },
            new() { Label = "最後發生", Value = item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss") },
            new() { Label = "使用模型", Value = result.ModelName },
        };
        if (result.Usage is { } usage)
        {
            meta.Add(new() { Label = "用量", Value = $"輸入 {usage.InputTokens:N0}　輸出 {usage.OutputTokens:N0} tokens" });
        }

        meta.Add(new() { Label = "耗時", Value = $"{result.Elapsed.TotalSeconds:F1} 秒" });
        return meta;
    }

    /// <summary>同畫面：額度用完記 warning、使用者關窗不記、其他記 error。</summary>
    private Exception LogFailure(Exception error, int id, CancellationToken cancellationToken)
    {
        if (error is AiQuotaExceededException)
        {
            logger.LogWarning("AI exception analysis blocked by AI quota. ExceptionLogId={ExceptionLogId}", id);
        }
        else if (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogError(error, "AI exception analysis failed. ExceptionLogId={ExceptionLogId}", id);
        }

        return error;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>稽核不記例外內容（可能含敏感資訊），只記編號、字數、模型與耗時（同畫面），另加 via=Api。</summary>
    private Task AuditAsync(string action, string targetId, string detail, bool success = true)
        => auditLogService.WriteForCurrentUserAsync(action, success: success, targetType: TargetType, targetId: targetId, detail: $"{detail}; via={AuditVia.Api}");
}
