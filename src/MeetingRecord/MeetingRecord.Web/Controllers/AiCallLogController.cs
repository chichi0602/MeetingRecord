using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.AiCallLogs;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// AI 對話紀錄（WASM 遷移 M5，1.1.38）：<c>api/v1/ai-call-logs</c>，**只限管理者**（同畫面）。
///
/// <para>
/// ⚠️ 內容含會議逐字稿與 AI 問答全文：**查看明細也寫稽核**（同畫面）。CSV 只有中繼資料、不含內容；
/// JSON、PDF 由伺服器產生並寫稽核。稽核只記編號與識別碼、不記內容，目標 <c>AiCallLog</c>，另加 <c>via=Api</c>。
/// </para>
/// </summary>
[Route("api/v1/ai-call-logs")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class AiCallLogController(
    AiCallLogService callLogService,
    IOptionsMonitor<AiCallLogSettings> callLogOptions,
    IPdfRenderer pdfRenderer,
    IAuditLogService auditLogService,
    IAuditActorProvider actorProvider,
    ILogger<AiCallLogController> logger) : ControllerBase
{
    private const string TargetType = "AiCallLog";

    /// <summary>頁面要顯示的設定：是否正在記錄、保留天數、CSV 上限。</summary>
    [HttpGet("meta")]
    public ActionResult<ApiResult<AiCallLogMetaDto>> Meta()
        => Ok(ApiResult<AiCallLogMetaDto>.SuccessResult(new AiCallLogMetaDto
        {
            Enabled = callLogOptions.CurrentValue.Enabled,
            RetentionDays = callLogOptions.CurrentValue.RetentionDays,
            MaxExportRows = AiCallLogService.MaxExportRows,
        }));

    /// <summary>篩選下拉：資料庫裡出現過的使用者與模型。</summary>
    [HttpGet("filter-options")]
    public async Task<ActionResult<ApiResult<AiCallLogFilterOptions>>> FilterOptions()
        => Ok(ApiResult<AiCallLogFilterOptions>.SuccessResult(await callLogService.GetFilterOptionsAsync()));

    /// <summary>清單一頁（可依時間、耗時、送出字元排序）。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<AiCallLogAdapterModel>>>> Search([FromBody] AiCallLogQuery query)
    {
        var result = await callLogService.GetAsync(query);
        var pageSize = Math.Max(1, query.PageSize);
        return Ok(ApiResult<PagedResult<AiCallLogAdapterModel>>.SuccessResult(new PagedResult<AiCallLogAdapterModel>
        {
            Items = [.. result.Result],
            TotalCount = result.Count,
            PageIndex = query.CurrentPage,
            PageSize = pageSize,
            TotalPages = (result.Count + pageSize - 1) / pageSize,
        }));
    }

    /// <summary>以呼叫識別碼找紀錄編號（從 Token 用量跳過來的 <c>?callId=</c>）；找不到 404。</summary>
    [HttpGet("by-call-id/{callId:guid}")]
    public async Task<ActionResult<ApiResult<int>>> FindByCallId(Guid callId)
        => await callLogService.FindIdByCallIdAsync(callId) is { } id
            ? Ok(ApiResult<int>.SuccessResult(id))
            : NotFound(ApiResult<int>.NotFoundResult("找不到這次呼叫的對話紀錄：可能已超過保留天數被清除、已手動刪除，或當時停用了記錄。"));

    /// <summary>明細：列、內容檔、同一次呼叫的用量、同一趟工作的其他呼叫。**寫稽核**（內容是逐字稿等敏感全文）。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResult<AiCallLogDetail>>> Detail(int id)
    {
        var detail = await callLogService.GetDetailAsync(id);
        if (detail is null)
        {
            return NotFound(ApiResult<AiCallLogDetail>.NotFoundResult("找不到這筆 AI 對話紀錄，可能已被刪除。"));
        }

        await AuditAsync(AuditActions.AiCallLogView, detail.Row.Id.ToString(), $"callId={detail.Row.CallId:N}; feature={detail.Row.Feature}");
        return Ok(ApiResult<AiCallLogDetail>.SuccessResult(detail));
    }

    /// <summary>下載內容檔原文（JSON）；內容檔不存在 404。寫稽核。</summary>
    [HttpGet("{id:int}/json")]
    public async Task<IActionResult> DownloadJson(int id)
    {
        var detail = await callLogService.GetDetailAsync(id);
        if (detail?.RawJson is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這筆紀錄的內容檔，可能已被刪除。"));
        }

        await AuditAsync(AuditActions.AiCallLogDownloadJson, detail.Row.Id.ToString(), $"callId={detail.Row.CallId:N}");
        return File(Encoding.UTF8.GetBytes(detail.RawJson), "application/json", $"MeetingRecord.Web-ai-call-{detail.Row.CallId:N}.json");
    }

    /// <summary>匯出 PDF：中繼資料、回應全文，附錄是送出內容。匯出者是 API 呼叫者。寫稽核。</summary>
    [HttpPost("{id:int}/pdf")]
    public async Task<IActionResult> ExportPdf(int id, CancellationToken cancellationToken)
    {
        var detail = await callLogService.GetDetailAsync(id);
        if (detail?.Content is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這筆紀錄的內容檔，可能已被刪除。"));
        }

        try
        {
            var exportedAt = DateTime.Now;
            var row = detail.Row;
            var html = DiagnosticReportExporter.BuildHtml(
                "AI 對話紀錄",
                [
                    ("時間", row.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("功能", row.FeatureText),
                    ("結果", row.OutcomeText),
                    ("使用者", row.UserName ?? "（系統）"),
                    ("關聯說明", row.RelatedInfo ?? "—"),
                    ("供應商／模型", $"{row.Provider}／{row.Model}"),
                    ("用量與費用", AiCallLogBlocks.DescribeUsage(detail.Usage)),
                    ("耗時", row.ElapsedText),
                    ("呼叫識別碼", row.CallId.ToString()),
                    ("匯出者", actorProvider.GetActor().Account ?? string.Empty),
                ],
                detail.Content.Response.Text,
                "附錄：送出內容",
                string.Join("\n\n", AiCallLogBlocks.Build(detail, AiCallLogViewMode.Request).Select(x => $"【{x.Label}】\n{x.Content}")),
                exportedAt);

            var pdf = await pdfRenderer.RenderAsync(html, cancellationToken);
            await AuditAsync(AuditActions.AiCallLogExportPdf, row.Id.ToString(), $"callId={row.CallId:N}; bytes={pdf.Length}");
            return File(pdf, "application/pdf", DiagnosticReportExporter.BuildFileName("AI對話紀錄", exportedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI call log PDF export through API failed. Id={Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult.FailureResult($"匯出 PDF 失敗：{ex.Message}", StatusCodes.Status500InternalServerError));
        }
    }

    /// <summary>匯出 CSV（目前條件、最新的優先，最多 <see cref="AiCallLogService.MaxExportRows"/> 筆）：只有中繼資料。寫稽核。</summary>
    [HttpPost("export/csv")]
    public async Task<IActionResult> ExportCsv([FromBody] AiCallLogQuery query)
    {
        query.CurrentPage = 1;
        query.PageSize = AiCallLogService.MaxExportRows;
        var items = await callLogService.GetForExportAsync(query);
        var bytes = CsvExport.ToCsv(AiCallLogCsv.Columns, items);
        await AuditAsync(AuditActions.AiCallLogExport, "*", $"format=csv; rows={items.Count}");
        return File(bytes, CsvExport.ContentType, $"MeetingRecord.Web-ai-call-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    /// <summary>刪除一筆（內容檔一併刪除；批次刪除由畫面逐筆呼叫）。不存在 404。寫稽核。</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(int id)
    {
        var detail = await callLogService.GetDetailAsync(id);
        if (detail is null)
        {
            return NotFound(ApiResult<object>.NotFoundResult("找不到要刪除的 AI 對話紀錄。"));
        }

        var result = await callLogService.DeleteAsync(id);
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await AuditAsync(AuditActions.AiCallLogDelete, id.ToString(), $"callId={detail.Row.CallId:N}; feature={detail.Row.Feature}");
        return Ok(ApiResult<object>.SuccessResult(null!, "刪除成功"));
    }

    /// <summary>清除某日之前（不含當天）的紀錄與內容檔，回筆數。寫稽核。</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<ApiResult<int>>> Purge([FromBody] AiCallLogPurgeRequestDto request, CancellationToken cancellationToken)
    {
        var before = request.Before.Date;
        var removed = await callLogService.PurgeBeforeAsync(before, cancellationToken);
        await AuditAsync(AuditActions.AiCallLogPurge, "*", $"before={before:yyyy-MM-dd}; rows={removed}");
        return Ok(ApiResult<int>.SuccessResult(removed));
    }

    /// <summary>清空全部紀錄與整個內容檔目錄。寫稽核。</summary>
    [HttpPost("clear-all")]
    public async Task<ActionResult<ApiResult<object>>> ClearAll()
    {
        var result = await callLogService.ClearAllAsync();
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await AuditAsync(AuditActions.AiCallLogClearAll, "*", result.Message);
        return Ok(ApiResult<object>.SuccessResult(null!, "已清空全部 AI 對話紀錄"));
    }

    /// <summary>稽核只記編號與識別碼，<b>不記內容</b>（同畫面），另加 via=Api。</summary>
    private Task AuditAsync(string action, string targetId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: TargetType, targetId: targetId, detail: $"{detail}; via={AuditVia.Api}");
}
