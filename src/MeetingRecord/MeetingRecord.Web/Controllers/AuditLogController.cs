using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.AuditLogs;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 稽核紀錄（WASM 遷移 M5，1.1.43）：<c>api/v1/audit-logs</c>，**只限管理者**（同畫面）。
///
/// <para>
/// 查詢、動作選項、CSV、清除 N 天前、全部清除；刻意<b>沒有</b>單筆刪除（同畫面）。匯出與清除本身也寫稽核，
/// 而且清除類一律在刪除<b>之後</b>才寫，這筆才會留在清空後的表裡（同畫面）。目標 <c>AuditLog</c>，另加 <c>via=Api</c>。
/// </para>
/// <para>時間：資料庫存 UTC，查詢條件與回傳一律是伺服器本地時間；轉換集中在 <see cref="AuditLogQueryService"/>。</para>
/// </summary>
[Route("api/v1/audit-logs")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AdminOnly]
public sealed class AuditLogController(
    AuditLogQueryService queryService,
    IAuditLogService auditLogService,
    IOptionsMonitor<LogRetentionSettings> retentionOptions) : ControllerBase
{
    /// <summary>頁面要顯示的設定：自動清除天數、手動清除天數、CSV 上限。</summary>
    [HttpGet("meta")]
    public ActionResult<ApiResult<AuditLogMetaDto>> Meta()
        => Ok(ApiResult<AuditLogMetaDto>.SuccessResult(new AuditLogMetaDto
        {
            RetentionDays = retentionOptions.CurrentValue.AuditLogDays,
            PurgeDays = retentionOptions.CurrentValue.ManualAuditLogDays,
            MaxExportRows = AuditLogQueryService.MaxExportRows,
        }));

    /// <summary>動作下拉：資料庫裡出現過的動作代碼。</summary>
    [HttpGet("actions")]
    public async Task<ActionResult<ApiResult<List<string>>>> Actions()
        => Ok(ApiResult<List<string>>.SuccessResult([.. await queryService.GetDistinctActionsAsync()]));

    /// <summary>清單一頁（只有發生時間可排序）。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<AuditLogAdapterModel>>>> Search([FromBody] AuditLogQuery query)
    {
        var result = await queryService.GetAsync(query);
        var pageSize = Math.Max(1, query.PageSize);
        return Ok(ApiResult<PagedResult<AuditLogAdapterModel>>.SuccessResult(new PagedResult<AuditLogAdapterModel>
        {
            Items = [.. result.Result],
            TotalCount = result.Count,
            PageIndex = query.CurrentPage,
            PageSize = pageSize,
            TotalPages = (result.Count + pageSize - 1) / pageSize,
        }));
    }

    /// <summary>匯出 CSV（目前條件、最新的優先，最多 <see cref="AuditLogQueryService.MaxExportRows"/> 筆）。寫稽核。</summary>
    [HttpPost("export/csv")]
    public async Task<IActionResult> ExportCsv([FromBody] AuditLogQuery query)
    {
        query.CurrentPage = 1;
        query.PageSize = AuditLogQueryService.MaxExportRows;
        var items = await queryService.GetForExportAsync(query);
        var bytes = CsvExport.ToCsv(AuditLogCsv.Columns, items);
        await WriteSelfAuditAsync(AuditActions.AuditExport, $"format=csv; rows={items.Count}");
        return File(bytes, CsvExport.ContentType, $"MeetingRecord-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    /// <summary>清除 N 天前的紀錄（N 由伺服器設定決定，同畫面）。稽核在刪除<b>之後</b>才寫。</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<ApiResult<object>>> Purge()
    {
        var days = retentionOptions.CurrentValue.ManualAuditLogDays;
        var result = await queryService.PurgeAsync(days);
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await WriteSelfAuditAsync(AuditActions.AuditPurge, $"days={days}; {result.Message}");
        return Ok(ApiResult<object>.SuccessResult(null!, result.Message));
    }

    /// <summary>清空全部稽核紀錄。稽核在刪除<b>之後</b>才寫，這筆會是清空後的第一筆。</summary>
    [HttpPost("clear-all")]
    public async Task<ActionResult<ApiResult<object>>> ClearAll()
    {
        var result = await queryService.ClearAllAsync();
        if (!result.Success)
        {
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        await WriteSelfAuditAsync(AuditActions.AuditClearAll, result.Message);
        return Ok(ApiResult<object>.SuccessResult(null!, "已清空全部稽核紀錄"));
    }

    /// <summary>本頁自己的稽核（匯出、清除），另加 via=Api。⚠️ 清除類必須在刪除之後才呼叫。</summary>
    private Task WriteSelfAuditAsync(string action, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: "AuditLog", targetId: "*", detail: $"{detail}; via={AuditVia.Api}");
}
