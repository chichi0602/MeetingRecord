using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Services;
using MeetingRecord.Dtos.AiCallLogs;

namespace MeetingRecord.Web.Components.Views.AiManagement;

/// <summary>
/// AI 對話紀錄（1.0.30，移植自 NET10-Blazor-Starter 的 /ai-call-logs，畫面依本專案規範）。**只限管理者**。
///
/// <para>資料由 <c>AiCallGateway</c> 在每次付費 AI 呼叫時寫入（1.0.29 起）。本頁負責查詢、明細（完整送出內容與回應）、
/// 匯出 CSV（不含內容）、下載 JSON、匯出 PDF 與刪除／清除。⚠️ 內容含會議逐字稿與 AI 問答全文：查看明細也寫稽核。</para>
/// </summary>
public partial class AiCallLogView : ComponentBase
{
    private const string PdfContentType = "application/pdf";
    private const string JsonContentType = "application/json";

    private const int MaxDisplayCharacters = AiCallLogBlocks.MaxDisplayCharacters;

    [Parameter]
    public string? CallId { get; set; }

    [Inject]
    private AiCallLogService AiCallLogService { get; set; } = default!;

    [Inject]
    private IOptionsMonitor<AiCallLogSettings> CallLogOptions { get; set; } = default!;

    [Inject]
    private IPdfRenderer PdfRenderer { get; set; } = default!;

    [Inject]
    private FileDownloadInterop FileDownloadInterop { get; set; } = default!;

    [Inject]
    private ClipboardInterop ClipboardInterop { get; set; } = default!;

    [Inject]
    private IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private NotificationService NotificationService { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<AiCallLogView> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private bool isAccessChecked;
    private bool isLoading;
    private bool deepLinkHandled;

    // ───────── 篩選 ─────────
    private DateTime? startTime;
    private DateTime? endTime;
    private string featureFilter = string.Empty;
    private string outcomeFilter = string.Empty;
    private string userFilter = string.Empty;
    private string modelFilter = string.Empty;
    private string keyword = string.Empty;
    private DateTime? purgeBefore;
    private AiCallLogFilterOptions filterOptions = new([], []);
    private string sortField = string.Empty;
    private bool? sortDescending;

    private List<AiCallLogAdapterModel> rows = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    // ───────── 明細 ─────────
    private bool detailVisible;
    private bool isDetailLoading;
    private bool isExporting;
    private AiCallLogDetail? detail;
    private AiCallLogViewMode viewMode = AiCallLogViewMode.Conversation;
    private bool renderMarkdown = true;

    private string RetentionText => CallLogOptions.CurrentValue.Enabled
        ? $"紀錄保留 {CallLogOptions.CurrentValue.RetentionDays} 天，每天自動清除。"
        : $"目前已停用記錄（AiCallLogSettings:Enabled = false），既有紀錄仍保留 {CallLogOptions.CurrentValue.RetentionDays} 天。";

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 只限管理者：權限鍵刻意不在角色權限目錄裡。判定前不讀任何資料。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/ai-call-logs");
            Logger.LogWarning("AI call log view denied because the current user is not an administrator.");
            return;
        }

        await LoadFilterOptionsAsync();
        await ReloadAsync();
    }

    /// <summary>從 Token 用量跳過來（<c>?callId=</c>）時，載入完成後直接打開那一筆。</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (deepLinkHandled || !isAccessChecked || !string.IsNullOrEmpty(RoleMessage) || string.IsNullOrWhiteSpace(CallId))
        {
            return;
        }

        deepLinkHandled = true;

        var id = Guid.TryParse(CallId, out var callId) ? await AiCallLogService.FindIdByCallIdAsync(callId) : null;
        if (id is null)
        {
            _ = MessageService.WarningAsync("找不到這次呼叫的對話紀錄：可能已超過保留天數被清除、已手動刪除，或當時停用了記錄。");
            return;
        }

        await OpenDetailAsync(id.Value);
    }

    // ───────── 查詢 ─────────

    private async Task LoadFilterOptionsAsync()
    {
        try
        {
            filterOptions = await AiCallLogService.GetFilterOptionsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load AI call log filter options.");
        }
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await LoadFilterOptionsAsync();
        await ReloadAsync();
    }

    /// <summary>
    /// 重新載入目前頁。開頭的早退不可省：兩次載入重疊會讓同一個 circuit 的 DbContext 被並行使用。
    /// </summary>
    private async Task ReloadAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        StateHasChanged();

        try
        {
            var result = await AiCallLogService.GetAsync(BuildQuery(_pageIndex, _pageSize));
            rows = result.Result.ToList();
            _total = result.Count;

            // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照 TodoViewView）。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await AiCallLogService.GetAsync(BuildQuery(_pageIndex, _pageSize));
                rows = result.Result.ToList();
                _total = result.Count;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load AI call logs.");
            _ = MessageService.ErrorAsync($"載入 AI 對話紀錄失敗：{ex.Message}");
            rows = [];
            _total = 0;
        }
        finally
        {
            // 全選只算這一頁（1.0.53）：換頁、排序、搜尋、刪除後都會走到這裡，勾選一律清掉。
            selectedRows = [];
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnTableChange(QueryModel<AiCallLogAdapterModel> args)
    {
        _pageIndex = args.PageIndex;
        _pageSize = args.PageSize;

        var sort = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
        sortField = sort?.FieldName ?? string.Empty;
        sortDescending = sort is null ? null : sort.SortDirection == SortDirection.Descending;

        await ReloadAsync();
    }

    private AiCallLogQuery BuildQuery(int page, int pageSize) => new()
    {
        StartTime = startTime?.Date,
        EndTime = endTime?.Date.AddDays(1).AddTicks(-1),
        Feature = Enum.TryParse<AiUsageFeature>(featureFilter, out var feature) ? feature : null,
        Outcome = Enum.TryParse<AiUsageOutcome>(outcomeFilter, out var outcome) ? outcome : null,
        UserId = int.TryParse(userFilter, out var userId) ? userId : null,
        Model = modelFilter ?? string.Empty,
        Keyword = keyword ?? string.Empty,
        CurrentPage = page,
        PageSize = pageSize,
        SortField = sortField,
        SortDescending = sortDescending,
    };

    private static string GetOutcomeClass(AiCallLogAdapterModel row) => row.Outcome switch
    {
        AiUsageOutcome.Succeeded => "call-log-pill",
        AiUsageOutcome.Cancelled => "call-log-pill call-log-pill-warn",
        _ => "call-log-pill call-log-pill-err",
    };

    // ───────── 明細 ─────────

    private Task OnViewAsync(AiCallLogAdapterModel row) => OpenDetailAsync(row.Id);

    private async Task OpenDetailAsync(int id)
    {
        if (isDetailLoading)
        {
            return;
        }

        isDetailLoading = true;
        detailVisible = true;
        StateHasChanged();

        try
        {
            detail = await AiCallLogService.GetDetailAsync(id);
            if (detail is null)
            {
                detailVisible = false;
                _ = MessageService.WarningAsync("找不到這筆 AI 對話紀錄，可能已被刪除。");
                return;
            }

            viewMode = AiCallLogViewMode.Conversation;
            renderMarkdown = AiCallLogBlocks.Build(detail, AiCallLogViewMode.Conversation).All(x => x.Content.Length <= MaxDisplayCharacters);

            // 內容含逐字稿與問答全文：誰在什麼時候看了哪一筆要留下來。
            await WriteAuditAsync(AuditActions.AiCallLogView, detail.Row.Id.ToString(), $"callId={detail.Row.CallId:N}; feature={detail.Row.Feature}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load AI call log detail. Id={Id}", id);
            _ = MessageService.ErrorAsync($"讀取明細失敗：{ex.Message}");
            detailVisible = false;
        }
        finally
        {
            isDetailLoading = false;
            StateHasChanged();
        }
    }

    private void OnDetailCancel() => detailVisible = false;

    private IReadOnlyList<AiCallLogDisplayBlock> CurrentBlocks => detail is null ? [] : AiCallLogBlocks.Build(detail, viewMode);

    private static string Truncate(string text) => AiCallLogBlocks.Truncate(text);

    private string UsageText => AiCallLogBlocks.DescribeUsage(detail?.Usage);

    private async Task OnCopyBlockAsync(string text)
    {
        if (await ClipboardInterop.CopyTextAsync(text))
        {
            _ = MessageService.SuccessAsync("已複製到剪貼簿。");
        }
        else
        {
            _ = MessageService.WarningAsync("瀏覽器不允許這次複製，請直接選取內容後按 Ctrl+C。");
        }
    }

    private async Task OnDownloadJsonAsync()
    {
        if (detail?.RawJson is null || isExporting)
        {
            return;
        }

        isExporting = true;
        try
        {
            var fileName = $"MeetingRecord.Web-ai-call-{detail.Row.CallId:N}.json";
            await FileDownloadInterop.SaveBytesAsync(fileName, Encoding.UTF8.GetBytes(detail.RawJson), JsonContentType);
            await WriteAuditAsync(AuditActions.AiCallLogDownloadJson, detail.Row.Id.ToString(), $"callId={detail.Row.CallId:N}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI call log JSON download failed. Id={Id}", detail.Row.Id);
            _ = MessageService.ErrorAsync($"下載失敗：{ex.Message}");
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    private async Task OnExportPdfAsync()
    {
        if (detail?.Content is null || isExporting)
        {
            return;
        }

        isExporting = true;
        StateHasChanged();

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
                    ("用量與費用", UsageText),
                    ("耗時", row.ElapsedText),
                    ("呼叫識別碼", row.CallId.ToString()),
                    ("匯出者", CurrentUserService.CurrentUser.Account),
                ],
                detail.Content.Response.Text,
                "附錄：送出內容",
                string.Join("\n\n", AiCallLogBlocks.Build(detail, AiCallLogViewMode.Request).Select(x => $"【{x.Label}】\n{x.Content}")),
                exportedAt);

            var pdf = await PdfRenderer.RenderAsync(html);
            var fileName = DiagnosticReportExporter.BuildFileName("AI對話紀錄", exportedAt);
            await FileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);

            await WriteAuditAsync(AuditActions.AiCallLogExportPdf, row.Id.ToString(), $"callId={row.CallId:N}; bytes={pdf.Length}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI call log PDF export failed. Id={Id}", detail.Row.Id);
            _ = MessageService.ErrorAsync($"匯出 PDF 失敗：{ex.Message}");
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    // ───────── 匯出、刪除、清除 ─────────

    private async Task OnExportAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            var items = await AiCallLogService.GetForExportAsync(BuildQuery(1, AiCallLogService.MaxExportRows));
            var bytes = CsvExport.ToCsv(AiCallLogCsv.Columns, items);
            var fileName = $"MeetingRecord.Web-ai-call-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            await FileDownloadInterop.SaveBytesAsync(fileName, bytes, CsvExport.ContentType);

            await WriteAuditAsync(AuditActions.AiCallLogExport, "*", $"format=csv; rows={items.Count}");
            NotifyDownloaded(fileName);
            if (items.Count >= AiCallLogService.MaxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {AiCallLogService.MaxExportRows:N0} 筆，請縮小查詢條件。");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI call log export failed.");
            _ = MessageService.ErrorAsync($"匯出失敗：{ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnDeleteAsync(AiCallLogAdapterModel row)
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "刪除 AI 對話紀錄",
            Content = "確定要刪除這一筆嗎？送出內容與回應的檔案會一併刪除，無法復原。Token 用量不受影響。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await AiCallLogService.DeleteAsync(row.Id);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.AiCallLogDelete, row.Id.ToString(), $"callId={row.CallId:N}; feature={row.Feature}");
            _ = MessageService.SuccessAsync("刪除成功");
            await ReloadAsync();
        }
        else
        {
            _ = MessageService.ErrorAsync(result.Message);
        }
    }

    #region 多選刪除（1.0.53）

    /// <summary>目前這一頁勾選的列；換頁、排序、搜尋、重新載入時清掉（ReloadAsync）。</summary>
    private IEnumerable<AiCallLogAdapterModel> selectedRows = [];

    private bool isBatchDeleting;

    private async Task OnBatchDeleteAsync()
    {
        var items = selectedRows.ToList();
        if (items.Count == 0 || isBatchDeleting || isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "刪除 AI 對話紀錄",
            Content = $"確定要刪除勾選的 {items.Count} 筆嗎？送出內容與回應的檔案會一併刪除，無法復原。Token 用量不受影響。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });
        if (!confirmed)
        {
            return;
        }

        isBatchDeleting = true;
        try
        {
            // 比照單筆：每刪成一筆就記一筆稽核。
            var result = await BatchRunner.RunAsync(items, x => $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss} {x.FeatureText}", async x =>
            {
                var r = await AiCallLogService.DeleteAsync(x.Id);
                if (!r.Success)
                {
                    return r.Message;
                }

                await WriteAuditAsync(AuditActions.AiCallLogDelete, x.Id.ToString(), $"callId={x.CallId:N}; feature={x.Feature}");
                return null;
            });
            Logger.LogInformation("Batch AI call log delete completed. Succeeded={Succeeded}, Failed={Failed}", result.Succeeded, result.Failures.Count);
            BatchResultNotifier.Notify(NotificationService, result, "刪除");
        }
        finally
        {
            isBatchDeleting = false;
        }

        await ReloadAsync();
    }

    #endregion

    private async Task OnPurgeAsync()
    {
        if (isLoading || purgeBefore is null || !AuthenticationStateHelper.CheckIsAdmin())
        {
            if (purgeBefore is null)
            {
                _ = MessageService.WarningAsync("請先選擇日期。");
            }

            return;
        }

        var before = purgeBefore.Value.Date;
        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = $"清除 {before:yyyy-MM-dd} 之前的 AI 對話紀錄",
            Content = $"將刪除 {before:yyyy-MM-dd} 之前（不含當天）的所有紀錄與內容檔，無法復原。Token 用量不受影響。",
            OkText = "清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        try
        {
            var removed = await AiCallLogService.PurgeBeforeAsync(before);
            await WriteAuditAsync(AuditActions.AiCallLogPurge, "*", $"before={before:yyyy-MM-dd}; rows={removed}");
            _ = MessageService.SuccessAsync($"已清除 {removed:N0} 筆。");
            _pageIndex = 1;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge AI call logs.");
            _ = MessageService.ErrorAsync($"清除失敗：{ex.Message}");
        }
    }

    private async Task OnClearAllAsync()
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "清空全部 AI 對話紀錄",
            Content = "將刪除所有紀錄與整個內容檔目錄，無法復原。Token 用量不受影響。",
            OkText = "全部清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await AiCallLogService.ClearAllAsync();
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.AiCallLogClearAll, "*", result.Message);
            _ = MessageService.SuccessAsync("已清空全部 AI 對話紀錄");
            _pageIndex = 1;
            await OnRefreshAsync();
        }
        else
        {
            _ = MessageService.ErrorAsync(result.Message);
        }
    }

    private void NotifyDownloaded(string fileName)
        => _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });

    /// <summary>稽核只記編號與識別碼，<b>不記內容</b>（內容是逐字稿等敏感全文）。</summary>
    private Task WriteAuditAsync(string action, string targetId, string detail)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: currentUser.Id,
            actorAccount: currentUser.Account,
            targetType: "AiCallLog",
            targetId: targetId,
            detail: detail);
    }
}
