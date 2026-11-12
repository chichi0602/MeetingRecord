using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.AiUsage;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// Token 用量（1.1.36）：<c>api/v1/ai-usage</c>，**只限管理者**（同畫面：看得到全公司的用量與人名、不套團隊過濾）。
///
/// <para>
/// 規則都在 <see cref="AiUsageAnalysisService"/>；CSV、PDF 改由伺服器產生（以前在畫面）。
/// 畫面以前自己寫的稽核改由這裡寫：動作代碼與內容不變，目標 <c>AiUsageLog</c>，另加 <c>via=Api</c>。
/// </para>
/// </summary>
[Route("api/v1/ai-usage")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class AiUsageController(
    AiUsageAnalysisService usageService,
    IAiQuotaService quotaService,
    AiCallLogService callLogService,
    IOptionsMonitor<AiQuotaSettings> quotaOptions,
    IOptionsMonitor<LogRetentionSettings> retentionOptions,
    IOptions<SystemSettings> systemSettings,
    IPdfRenderer pdfRenderer,
    IAuditLogService auditLogService,
    IAuditActorProvider actorProvider,
    ILogger<AiUsageController> logger) : ControllerBase
{
    private const string TargetType = "AiUsageLog";

    /// <summary>上限列列出本月用量最高的前幾人。</summary>
    private const int TopUserCount = 5;

    /// <summary>本月概況（固定全部功能、本月至今，不跟著篩選走）、上限列與頁面要顯示的設定。</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResult<AiUsageOverviewDto>>> Overview(CancellationToken cancellationToken)
    {
        var perUserLimit = quotaOptions.CurrentValue.PerUserMonthlyTwd;
        var overview = new AiUsageOverviewDto
        {
            Summary = await usageService.GetSummaryAsync(cancellationToken: cancellationToken),
            PerUserLimit = perUserLimit,
            RetentionDays = retentionOptions.CurrentValue.AiUsageLogDays,
            MaxExportRows = AiUsageAnalysisService.MaxExportRows,
        };

        // 上限列讀不到時只記錯誤、不擋整頁（上限是護欄，不是這一頁的主體）。
        try
        {
            overview.GlobalQuota = (await quotaService.GetStatusAsync(null, cancellationToken)).FirstOrDefault(x => x.Scope == AiQuotaScope.Global);
            overview.TopUsers = perUserLimit > 0 ? [.. await quotaService.GetTopUsersAsync(TopUserCount, cancellationToken)] : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Loading AI quota status through API failed.");
        }

        return Ok(ApiResult<AiUsageOverviewDto>.SuccessResult(overview));
    }

    /// <summary>篩選下拉的選項：帳本裡出現過的使用者（最近一次的名稱）與模型。</summary>
    [HttpGet("filter-options")]
    public async Task<ActionResult<ApiResult<AiUsageFilterOptions>>> FilterOptions(CancellationToken cancellationToken)
        => Ok(ApiResult<AiUsageFilterOptions>.SuccessResult(await usageService.GetFilterOptionsAsync(cancellationToken)));

    /// <summary>篩選範圍的統計：合計、每日、依使用者／功能／模型、圖表切片。</summary>
    [HttpPost("report")]
    public async Task<ActionResult<ApiResult<AiUsageReport>>> Report([FromBody] AiUsageFilter filter, CancellationToken cancellationToken)
        => Ok(ApiResult<AiUsageReport>.SuccessResult(await usageService.GetReportAsync(filter, cancellationToken)));

    /// <summary>明細表的一頁（只能依時間排序）。</summary>
    [HttpPost("rows")]
    public async Task<ActionResult<ApiResult<AiUsagePagedResult>>> Rows([FromBody] AiUsageQuery query, CancellationToken cancellationToken)
        => Ok(ApiResult<AiUsagePagedResult>.SuccessResult(await usageService.GetRecentCallsAsync(query, cancellationToken)));

    /// <summary>單筆明細，連同同一次呼叫的 AI 對話紀錄編號（沒有則 null）。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResult<AiUsageDetailResponseDto>>> Detail(int id, CancellationToken cancellationToken)
    {
        var detail = await usageService.GetDetailAsync(id, cancellationToken);
        if (detail is null)
        {
            return NotFound(ApiResult<AiUsageDetailResponseDto>.NotFoundResult("找不到這筆用量紀錄，可能已被刪除。"));
        }

        int? callLogId = detail.CallId is { } callId ? await callLogService.FindIdByCallIdAsync(callId) : null;
        return Ok(ApiResult<AiUsageDetailResponseDto>.SuccessResult(new AiUsageDetailResponseDto { Detail = detail, CallLogId = callLogId }));
    }

    /// <summary>匯出 CSV（最多 <see cref="AiUsageAnalysisService.MaxExportRows"/> 筆，最新的優先）。</summary>
    [HttpPost("export/csv")]
    public async Task<IActionResult> ExportCsv([FromBody] AiUsageFilter filter, CancellationToken cancellationToken)
    {
        var items = await usageService.GetForExportAsync(filter, cancellationToken: cancellationToken);
        var bytes = CsvExport.ToCsv(AiUsageCsv.Columns, items);
        await AuditAsync(AuditActions.AiUsageExport, "*", $"format=csv; rows={items.Count}; range={RangeText(filter)}");
        return File(bytes, CsvExport.ContentType, $"MeetingRecord.Web-token-usage-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    /// <summary>匯出 PDF 報表：篩選條件、合計、分組與最多 <see cref="TokenUsageReportExporter.MaxDetailRows"/> 筆明細。</summary>
    [HttpPost("export/pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] AiUsageFilter filter, CancellationToken cancellationToken)
    {
        try
        {
            var exportedAt = DateTime.Now;
            var report = await usageService.GetReportAsync(filter, cancellationToken);

            // 多拿一筆：用來判斷明細是否超過上限、要不要在報表加註。
            var details = await usageService.GetForExportAsync(filter, TokenUsageReportExporter.MaxDetailRows + 1, cancellationToken);
            var html = TokenUsageReportExporter.BuildHtml(new TokenUsageReportRequest(
                systemSettings.Value.SystemInformation.SystemVersion,
                actorProvider.GetActor().Account ?? string.Empty,
                exportedAt,
                RangeText(filter),
                await DescribeFiltersAsync(filter, cancellationToken),
                report,
                details));

            var pdf = await pdfRenderer.RenderAsync(html, cancellationToken);
            await AuditAsync(AuditActions.AiUsageExportPdf, "*", $"rows={report.CallCount}; range={RangeText(filter)}; bytes={pdf.Length}");
            return File(pdf, "application/pdf", TokenUsageReportExporter.BuildFileName(exportedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Token usage PDF export through API failed.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult.FailureResult($"匯出 PDF 失敗：{ex.Message}", StatusCodes.Status500InternalServerError));
        }
    }

    /// <summary>刪除一筆（批次刪除由畫面逐筆呼叫）。本月的紀錄是用量上限的計算來源，確認框在畫面上。</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(int id, CancellationToken cancellationToken)
    {
        var detail = await usageService.GetDetailAsync(id, cancellationToken);
        if (detail is null || !await usageService.DeleteAsync(id, cancellationToken))
        {
            return NotFound(ApiResult<object>.NotFoundResult("找不到這筆紀錄"));
        }

        await AuditAsync(AuditActions.AiUsageDelete, id.ToString(), $"feature={detail.Feature}; callId={detail.CallId:N}");
        return Ok(ApiResult<object>.SuccessResult(null!, "刪除成功"));
    }

    /// <summary>清除某日之前（不含當天）的所有用量紀錄，回刪除筆數。</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<ApiResult<int>>> Purge([FromBody] AiUsagePurgeRequestDto request, CancellationToken cancellationToken)
    {
        var before = request.Before.Date;
        var removed = await usageService.PurgeBeforeAsync(before, cancellationToken);
        await AuditAsync(AuditActions.AiUsagePurge, "*", $"before={before:yyyy-MM-dd}; rows={removed}");
        return Ok(ApiResult<int>.SuccessResult(removed));
    }

    /// <summary>清空全部用量紀錄，回刪除筆數。統計起始日會重新計算、本月已用額度歸零。</summary>
    [HttpPost("clear-all")]
    public async Task<ActionResult<ApiResult<int>>> ClearAll(CancellationToken cancellationToken)
    {
        var removed = await usageService.ClearAllAsync(cancellationToken);
        await AuditAsync(AuditActions.AiUsageClearAll, "*", $"rows={removed}");
        return Ok(ApiResult<int>.SuccessResult(removed));
    }

    /// <summary>區間文字（同畫面）：「yyyy/MM/dd～yyyy/MM/dd」，結束日是不含的上界減一天。</summary>
    internal static string RangeText(AiUsageFilter filter) => filter.From is null && filter.ToExclusive is null
        ? "不限"
        : $"{filter.From?.ToString("yyyy/MM/dd") ?? "不限"}～{filter.ToExclusive?.AddDays(-1).ToString("yyyy/MM/dd") ?? "不限"}";

    /// <summary>報表表頭的篩選條件（區間以外）；沒有選的不列。使用者名稱取篩選下拉的名稱。</summary>
    private async Task<IReadOnlyList<(string Label, string Value)>> DescribeFiltersAsync(AiUsageFilter filter, CancellationToken cancellationToken)
    {
        var list = new List<(string, string)>();
        if (filter.Feature is { } feature)
        {
            list.Add(("功能", AiUsageFeatureText.Describe(feature)));
        }

        if (filter.UserId is { } userId)
        {
            var options = await usageService.GetFilterOptionsAsync(cancellationToken);
            var name = options.Users.FirstOrDefault(x => x.UserId == userId)?.UserName;
            list.Add(("使用者", string.IsNullOrWhiteSpace(name) ? $"#{userId}" : name));
        }

        if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            list.Add(("模型", filter.Model));
        }

        if (filter.Outcome is { } outcome)
        {
            list.Add(("結果", AiUsageOutcomeText.Describe(outcome)));
        }

        return list;
    }

    /// <summary>稽核只記編號、筆數與條件（同畫面），另加 via=Api。</summary>
    private Task AuditAsync(string action, string targetId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: TargetType, targetId: targetId, detail: $"{detail}; via={AuditVia.Api}");
}
