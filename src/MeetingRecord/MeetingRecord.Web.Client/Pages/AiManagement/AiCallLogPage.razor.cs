using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.AiCallLogs;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.AiManagement;

/// <summary>
/// AI 對話紀錄（1.0.30，移植自 NET10-Blazor-Starter 的 /ai-call-logs，畫面依本專案規範）。**只限管理者**。
///
/// <para>資料由 <c>AiCallGateway</c> 在每次付費 AI 呼叫時寫入（1.0.29 起）。本頁負責查詢、明細（完整送出內容與回應）、
/// 匯出 CSV（不含內容）、下載 JSON、匯出 PDF 與刪除／清除，全部走 <c>api/v1/ai-call-logs</c>（1.1.38）。
/// ⚠️ 內容含會議逐字稿與 AI 問答全文：查看明細也寫稽核——由伺服器寫，這裡不寫。</para>
/// </summary>
public partial class AiCallLogPage : ComponentBase
{
    private const int MaxDisplayCharacters = AiCallLogBlocks.MaxDisplayCharacters;

    /// <summary>從 Token 用量的明細跳過來時帶的呼叫識別碼（<c>/ai-call-logs?callId=…</c>）。</summary>
    [SupplyParameterFromQuery(Name = "callId")]
    public string? CallId { get; set; }

    [Inject] private AiCallLogApi AiCallLogApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private FileDownloadInterop FileDownloadInterop { get; set; } = default!;
    [Inject] private ClipboardInterop ClipboardInterop { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private ILogger<AiCallLogPage> Logger { get; set; } = default!;

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
    private AiCallLogMetaDto? meta;
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

    private string RetentionText => meta is null
        ? string.Empty
        : meta.Enabled
            ? $"紀錄保留 {meta.RetentionDays} 天，每天自動清除。"
            : $"目前已停用記錄（AiCallLogSettings:Enabled = false），既有紀錄仍保留 {meta.RetentionDays} 天。";

    private int MaxExportRows => meta?.MaxExportRows ?? 10_000;

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 只限管理者：權限鍵刻意不在角色權限目錄裡。判定前不讀任何資料。API 也是 [AdminOnly]。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/ai-call-logs");
            Logger.LogWarning("AI call log view denied because the current user is not an administrator.");
            return;
        }

        await LoadMetaAsync();
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

        int? id = null;
        if (Guid.TryParse(CallId, out var callId))
        {
            var result = await AiCallLogApi.FindIdByCallIdAsync(callId);
            id = result.Success ? result.Data : null;
        }

        if (id is null)
        {
            _ = MessageService.WarningAsync("找不到這次呼叫的對話紀錄：可能已超過保留天數被清除、已手動刪除，或當時停用了記錄。");
            return;
        }

        await OpenDetailAsync(id.Value);
    }

    // ───────── 查詢 ─────────

    private async Task LoadMetaAsync()
    {
        var result = await AiCallLogApi.GetMetaAsync();
        if (result.Success)
        {
            meta = result.Data;
        }
        else
        {
            Logger.LogWarning("Failed to load AI call log settings: {Message}", ApiClient.ErrorText(result));
        }
    }

    private async Task LoadFilterOptionsAsync()
    {
        var result = await AiCallLogApi.GetFilterOptionsAsync();
        if (result.Success && result.Data is { } options)
        {
            filterOptions = options;
        }
        else
        {
            Logger.LogWarning("Failed to load AI call log filter options: {Message}", ApiClient.ErrorText(result));
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

    /// <summary>重新載入目前頁。重疊時忽略。</summary>
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
            var result = await AiCallLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
            if (!result.Success || result.Data is not { } page)
            {
                _ = MessageService.ErrorAsync($"載入 AI 對話紀錄失敗：{ApiClient.ErrorText(result)}");
                rows = [];
                _total = 0;
                return;
            }

            rows = page.Items.ToList();
            _total = page.TotalCount;

            // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照待辦頁）。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await AiCallLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
                rows = result.Data?.Items.ToList() ?? [];
                _total = result.Data?.TotalCount ?? 0;
            }
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

    /// <summary>讀明細。伺服器回傳時就寫了查看稽核（內容含逐字稿與問答全文）。</summary>
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
            var result = await AiCallLogApi.GetDetailAsync(id);
            if (result.StatusCode == 404 || (result.Success && result.Data is null))
            {
                detailVisible = false;
                _ = MessageService.WarningAsync("找不到這筆 AI 對話紀錄，可能已被刪除。");
                return;
            }

            if (!result.Success)
            {
                detailVisible = false;
                _ = MessageService.ErrorAsync($"讀取明細失敗：{ApiClient.ErrorText(result)}");
                return;
            }

            detail = result.Data!;
            viewMode = AiCallLogViewMode.Conversation;
            renderMarkdown = AiCallLogBlocks.Build(detail, AiCallLogViewMode.Conversation).All(x => x.Content.Length <= MaxDisplayCharacters);
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
            var download = await AiCallLogApi.DownloadJsonAsync(detail.Row.Id);
            await SaveDownloadAsync(download, "下載失敗");
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
            var download = await AiCallLogApi.ExportPdfAsync(detail.Row.Id);
            await SaveDownloadAsync(download, "匯出 PDF 失敗");
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    // ───────── 匯出、刪除、清除（伺服器執行、伺服器寫稽核） ─────────

    private async Task OnExportAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            var download = await AiCallLogApi.ExportCsvAsync(BuildQuery(1, MaxExportRows));
            if (await SaveDownloadAsync(download, "匯出失敗") && _total >= MaxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {MaxExportRows:N0} 筆，請縮小查詢條件。");
            }
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnDeleteAsync(AiCallLogAdapterModel row)
    {
        if (isLoading)
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

        var result = await AiCallLogApi.DeleteAsync(row.Id);
        if (result.Success)
        {
            _ = MessageService.SuccessAsync("刪除成功");
        }
        else if (result.StatusCode == 404)
        {
            _ = MessageService.WarningAsync("這筆紀錄已不存在。");
        }
        else
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        await ReloadAsync();
    }

    #region 多選刪除（1.0.53）

    /// <summary>目前這一頁勾選的列；換頁、排序、搜尋、重新載入時清掉（ReloadAsync）。</summary>
    private IEnumerable<AiCallLogAdapterModel> selectedRows = [];

    private bool isBatchDeleting;

    private async Task OnBatchDeleteAsync()
    {
        var items = selectedRows.ToList();
        if (items.Count == 0 || isBatchDeleting || isLoading)
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
            // 逐筆呼叫刪除（伺服器每刪成一筆就記一筆稽核，比照單筆）。
            var result = await BatchRunner.RunAsync(items, x => $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss} {x.FeatureText}", async x =>
            {
                var deleted = await AiCallLogApi.DeleteAsync(x.Id);
                return deleted.Success ? null : deleted.StatusCode == 404 ? "找不到這筆紀錄" : ApiClient.ErrorText(deleted);
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
        if (purgeBefore is null)
        {
            _ = MessageService.WarningAsync("請先選擇日期。");
            return;
        }

        if (isLoading)
        {
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

        var result = await AiCallLogApi.PurgeAsync(before);
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync($"清除失敗：{ApiClient.ErrorText(result)}");
            return;
        }

        _ = MessageService.SuccessAsync($"已清除 {result.Data:N0} 筆。");
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnClearAllAsync()
    {
        if (isLoading)
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

        var result = await AiCallLogApi.ClearAllAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        _ = MessageService.SuccessAsync("已清空全部 AI 對話紀錄");
        _pageIndex = 1;
        await OnRefreshAsync();
    }

    /// <summary>把伺服器產生的檔案存到瀏覽器；失敗就顯示「{失敗前綴}：原因」。回傳是否成功。</summary>
    private async Task<bool> SaveDownloadAsync(ApiDownload download, string failurePrefix)
    {
        if (!download.Success || download.Content is null)
        {
            _ = MessageService.ErrorAsync($"{failurePrefix}：{download.Error}");
            return false;
        }

        await FileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
        NotifyDownloaded(download.FileName!);
        return true;
    }

    private void NotifyDownloaded(string fileName)
        => _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });
}
