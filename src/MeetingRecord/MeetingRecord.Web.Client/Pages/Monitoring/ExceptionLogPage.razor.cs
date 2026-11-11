using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.Monitoring;

/// <summary>
/// 系統例外紀錄（0.4.122，移植自 NET10-Blazor-Starter，不含 Email 告警與 AI 追問；1.1.41 從 Blazor Server 的 ExceptionLogView
/// 搬到 WASM，畫面不變）。**管理者限定**。
///
/// <para>資料由伺服器的 <c>ExceptionLogProvider</c> → 背景寫入器自動收集；本頁負責查詢、明細、複製、匯出 CSV、
/// 清除與 AI 例外分析（會產生費用：先確認、邊生成邊顯示、關閉視窗即取消、寫入用量帳本），全部走 <c>api/v1/exception-logs</c>（1.1.40）。
/// 送給 AI 的內容由伺服器依例外編號組（不含帳號）；稽核都由伺服器寫，這裡不寫。</para>
/// </summary>
public partial class ExceptionLogPage : ComponentBase, IDisposable
{
    /// <summary>複製全部（每筆含完整堆疊）超過這個筆數先確認。</summary>
    private const int CopyAllConfirmThreshold = 100;

    private enum AiState
    {
        None,
        Running,
        Failed,
        Succeeded,
    }

    [Inject] private ExceptionLogApi ExceptionLogApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private FileDownloadInterop FileDownloadInterop { get; set; } = default!;
    [Inject] private ClipboardInterop ClipboardInterop { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private ILogger<ExceptionLogPage> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private bool isAccessChecked;
    private bool isLoading;
    private bool isDisposed;

    private DateTime? startTime;
    private DateTime? endTime;
    private string selectedSource = string.Empty;
    private string accountFilter = string.Empty;
    private string keyword = string.Empty;
    private string sortField = string.Empty;
    private bool? sortDescending;

    private List<ExceptionLogAdapterModel> rows = [];
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;

    private ExceptionLogMetaDto? meta;

    private bool detailVisible;
    private ExceptionLogAdapterModel? detailItem;
    private string detailStackTrace = string.Empty;
    private string? detailRawStackTrace;
    private int detailAiPromptChars;
    private bool isStackTraceLoading;

    // ───────── AI 分析 ─────────
    private bool aiModalVisible;
    private AiState aiState = AiState.None;
    private bool isAiRunning;
    private bool isAiExporting;
    private CancellationTokenSource? aiCts;
    private string aiStreaming = string.Empty;
    private string aiMarkdown = string.Empty;
    private string aiErrorMessage = string.Empty;
    private int aiElapsedSeconds;
    private List<DiagnosticAiMetaLineDto> aiMeta = [];

    private bool aiAvailable => meta?.AiAvailable == true;

    private int PurgeDays => meta?.PurgeDays ?? 90;

    private int MaxRows => meta?.MaxRows ?? 5000;

    private string RetentionText => meta is null
        ? string.Empty
        : meta.RetentionDays > 0
            ? $"「最後發生」超過 {meta.RetentionDays} 天的紀錄每天自動清除。"
            : "自動清除已停用（LogRetentionSettings:ExceptionLogDays = 0）。";

    private string AiButtonTitle => aiAvailable
        ? "把這筆例外（不含帳號）送給 AI 分析根因與修正建議（會產生費用）"
        : meta?.AiUnavailableReason ?? string.Empty;

    private string AiModalTitle => aiState switch
    {
        AiState.Running => "AI 例外分析（進行中）",
        AiState.Failed => "AI 例外分析（未完成）",
        _ => "AI 例外分析結果",
    };

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡。判定前不讀任何資料。API 也是 [AdminOnly]。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/system-exceptions");
            Logger.LogWarning("Exception log view denied because the current user is not an administrator.");
            return;
        }

        var metaResult = await ExceptionLogApi.GetMetaAsync();
        if (metaResult.Success)
        {
            meta = metaResult.Data;
        }
        else
        {
            Logger.LogWarning("Failed to load exception log settings: {Message}", ApiClient.ErrorText(metaResult));
        }

        await ReloadAsync();
    }

    // ───────── 查詢 ─────────

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
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
            var result = await ExceptionLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
            if (!result.Success || result.Data is not { } page)
            {
                _ = MessageService.ErrorAsync($"載入例外紀錄失敗：{ApiClient.ErrorText(result)}");
                rows = [];
                _total = 0;
                return;
            }

            rows = page.Items.ToList();
            _total = page.TotalCount;

            // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照 TodoViewView）。
            // ⚠️ Math.Max(1, ...) 不可省：PageIndex < 1 時 AntDesign 不會再觸發 OnChange。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await ExceptionLogApi.SearchAsync(BuildQuery(_pageIndex, _pageSize));
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

    private async Task OnTableChange(QueryModel<ExceptionLogAdapterModel> args)
    {
        _pageIndex = args.PageIndex;
        _pageSize = args.PageSize;

        var sort = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
        sortField = sort?.FieldName ?? string.Empty;
        sortDescending = sort is null ? null : sort.SortDirection == SortDirection.Descending;

        await ReloadAsync();
    }

    private ExceptionLogQuery BuildQuery(int page, int pageSize) => new()
    {
        StartTime = startTime,
        EndTime = endTime,
        Source = selectedSource ?? string.Empty,
        Account = accountFilter ?? string.Empty,
        Keyword = keyword ?? string.Empty,
        CurrentPage = page,
        PageSize = pageSize,
        SortField = sortField,
        SortDescending = sortDescending,
    };

    /// <summary>複製全部與匯出用：同一組條件、不分頁（上限＝資料表的列數上限）。</summary>
    private ExceptionLogQuery BuildFullQuery() => BuildQuery(1, MaxRows);

    private static string GetCountClass(long count) => count switch
    {
        >= 100 => "exception-log-count exception-log-count-high",
        >= 10 => "exception-log-count exception-log-count-mid",
        _ => "exception-log-count",
    };

    // ───────── 明細 ─────────

    private async Task OnViewAsync(ExceptionLogAdapterModel item)
    {
        detailItem = item;
        detailStackTrace = "讀取中…";
        detailRawStackTrace = null;
        detailAiPromptChars = 0;
        isStackTraceLoading = true;
        detailVisible = true;
        StateHasChanged();

        try
        {
            var result = await ExceptionLogApi.GetStackTraceAsync(item.Id);
            if (result.Success && result.Data is { } data)
            {
                detailRawStackTrace = data.StackTrace;
                detailAiPromptChars = data.AiPromptChars;
                detailStackTrace = detailRawStackTrace
                    ?? "堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。完整日誌請到「日誌檢視」以最後追蹤碼查詢。";
            }
            else
            {
                detailStackTrace = result.StatusCode == 404
                    ? "這筆例外紀錄已不存在，可能已被刪除。"
                    : $"讀取堆疊失敗：{ApiClient.ErrorText(result)}";
            }
        }
        finally
        {
            isStackTraceLoading = false;
            StateHasChanged();
        }
    }

    private void OnDetailCancel() => detailVisible = false;

    private Task OnCopyDetailAsync()
        => detailItem is null
            ? Task.CompletedTask
            : CopyAsync(ExceptionLogClipboardText.Build(detailItem, detailRawStackTrace), 1);

    // ───────── 複製、匯出、刪除、清除 ─────────

    /// <summary>點列複製該筆（含完整堆疊）。</summary>
    private async Task OnRowClickAsync(RowData<ExceptionLogAdapterModel> row)
    {
        if (isLoading)
        {
            return;
        }

        var result = await ExceptionLogApi.GetStackTraceAsync(row.Data.Id);
        if (!result.Success)
        {
            Logger.LogWarning("Failed to copy exception log. ExceptionLogId={ExceptionLogId}, Message={Message}", row.Data.Id, ApiClient.ErrorText(result));
            _ = MessageService.ErrorAsync("複製失敗，請改按「查看」再複製。");
            return;
        }

        await CopyAsync(ExceptionLogClipboardText.Build(row.Data, result.Data?.StackTrace), 1);
    }

    private async Task OnCopyAllAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            var search = await ExceptionLogApi.SearchAsync(BuildFullQuery());
            if (!search.Success)
            {
                _ = MessageService.ErrorAsync($"複製失敗：{ApiClient.ErrorText(search)}");
                return;
            }

            var items = search.Data?.Items.ToList() ?? [];
            if (items.Count == 0)
            {
                return;
            }

            if (items.Count > CopyAllConfirmThreshold)
            {
                var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
                {
                    Title = "確認複製",
                    Content = $"本次查詢共 {items.Count:N0} 筆（每筆含完整堆疊），內容較大，貼上時可能較慢；大量資料建議改用「匯出」。",
                    OkText = "仍要複製",
                    CancelText = "取消",
                    MaskClosable = false,
                });

                if (!confirmed)
                {
                    return;
                }
            }

            var stackTraces = await ExceptionLogApi.GetStackTracesAsync(items.Select(x => x.Id).ToList());
            if (!stackTraces.Success || stackTraces.Data is not { } stacks)
            {
                _ = MessageService.ErrorAsync($"複製失敗：{ApiClient.ErrorText(stackTraces)}");
                return;
            }

            await CopyAsync(ExceptionLogClipboardText.BuildMany(items.Select(x => (x, stacks.GetValueOrDefault(x.Id)))), items.Count);
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task CopyAsync(string text, int rowCount)
    {
        if (await ClipboardInterop.CopyTextAsync(text))
        {
            _ = MessageService.SuccessAsync($"已複製 {rowCount:N0} 筆例外紀錄到剪貼簿。");
        }
        else
        {
            _ = MessageService.WarningAsync("瀏覽器不允許這次複製，請在明細窗直接選取內容後按 Ctrl+C。");
        }
    }

    private async Task OnExportAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            await SaveDownloadAsync(await ExceptionLogApi.ExportCsvAsync(BuildFullQuery()), "匯出失敗");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnDeleteAsync(ExceptionLogAdapterModel item)
    {
        if (isLoading)
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "刪除例外紀錄",
            Content = "確定要刪除這一列嗎？相同的例外若再次發生，會重新出現並從 1 次開始計算。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await ExceptionLogApi.DeleteAsync(item.Id);
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
    private IEnumerable<ExceptionLogAdapterModel> selectedRows = [];

    private bool isBatchDeleting;

    private bool IsSelected(ExceptionLogAdapterModel row) => selectedRows.Any(x => x.Id == row.Id);

    private bool IsAllSelected => rows.Count > 0 && rows.All(IsSelected);

    private bool IsPartlySelected => selectedRows.Any() && !IsAllSelected;

    private void OnRowSelectedChanged(ExceptionLogAdapterModel row, bool selected)
        => selectedRows = selected
            ? [.. selectedRows.Where(x => x.Id != row.Id), row]
            : [.. selectedRows.Where(x => x.Id != row.Id)];

    /// <summary>表頭勾選＝全選這一頁。</summary>
    private void OnSelectAllChanged(bool selected) => selectedRows = selected ? [.. rows] : [];

    private async Task OnBatchDeleteAsync()
    {
        var items = selectedRows.ToList();
        if (items.Count == 0 || isBatchDeleting || isLoading)
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "刪除例外紀錄",
            Content = $"確定要刪除勾選的 {items.Count} 列嗎？相同的例外若再次發生，會重新出現並從 1 次開始計算。",
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
            // 逐筆呼叫刪除（伺服器每刪成一列就記一筆稽核，比照單筆）。
            var result = await BatchRunner.RunAsync(items, x => x.ShortExceptionType, async x =>
            {
                var deleted = await ExceptionLogApi.DeleteAsync(x.Id);
                return deleted.Success ? null : deleted.StatusCode == 404 ? "找不到這筆紀錄" : ApiClient.ErrorText(deleted);
            });
            Logger.LogInformation("Batch exception log delete completed. Succeeded={Succeeded}, Failed={Failed}", result.Succeeded, result.Failures.Count);
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
        if (isLoading)
        {
            return;
        }

        var days = PurgeDays;
        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = $"清除 {days} 天未再發生的紀錄",
            Content = $"將刪除「最後發生」早於 {days} 天前的所有紀錄與其堆疊檔。此動作無法復原。",
            OkText = "清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await ExceptionLogApi.PurgeAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        _ = MessageService.SuccessAsync(string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
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
            Title = "清空全部例外紀錄",
            Content = "將刪除所有紀錄與整個堆疊檔案目錄。此動作無法復原。",
            OkText = "全部清除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        var result = await ExceptionLogApi.ClearAllAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync(ApiClient.ErrorText(result));
            return;
        }

        _ = MessageService.SuccessAsync("已清空全部例外紀錄");
        _pageIndex = 1;
        await ReloadAsync();
    }

    /// <summary>把伺服器產生的檔案存到瀏覽器；失敗就顯示「{失敗前綴}：原因」。</summary>
    private async Task SaveDownloadAsync(ApiDownload download, string failurePrefix)
    {
        if (!download.Success || download.Content is null)
        {
            _ = MessageService.ErrorAsync($"{failurePrefix}：{download.Error}");
            return;
        }

        await FileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
        NotifyDownloaded(download.FileName!);
    }

    private void NotifyDownloaded(string fileName)
        => _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });

    // ───────── AI 例外分析（單次）─────────

    private async Task OnAiAnalyzeAsync()
    {
        if (isAiRunning || detailItem is null)
        {
            return;
        }

        isAiRunning = true;
        var item = detailItem;
        try
        {
            var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認執行 AI 例外分析（會產生費用）",
                Content = $"將把這筆例外的類型、訊息、頁面、操作與完整堆疊（約 {detailAiPromptChars:N0} 字，不含帳號）送到 Azure OpenAI 分析，會產生費用。確定要繼續嗎？",
                OkText = "開始分析",
                CancelText = "取消",
                MaskClosable = false,
            });

            if (!confirmed)
            {
                return;
            }

            await RunAiAnalysisAsync(item);
        }
        finally
        {
            aiCts?.Cancel();
            aiCts?.Dispose();
            aiCts = null;
            isAiRunning = false;
            if (!isDisposed)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>送出由伺服器依例外編號組（不含帳號）；成功、失敗、取消的稽核都由伺服器寫。</summary>
    private async Task RunAiAnalysisAsync(ExceptionLogAdapterModel item)
    {
        aiStreaming = string.Empty;
        aiMarkdown = string.Empty;
        aiErrorMessage = string.Empty;
        aiMeta = [];
        aiElapsedSeconds = 0;
        aiState = AiState.Running;
        aiModalVisible = true;
        StateHasChanged();

        aiCts = new CancellationTokenSource();
        var token = aiCts.Token;
        _ = RunElapsedTickerAsync(token);

        try
        {
            var done = await ExceptionLogApi.AnalyzeAsync(item.Id, OnAiDelta, token);
            if (isDisposed)
            {
                return;
            }

            aiMarkdown = done.Content ?? string.Empty;
            aiMeta = done.Meta ?? [];
            aiState = AiState.Succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者關掉視窗：請求中斷，伺服器停止呼叫 AI（帳本記成 Cancelled、稽核記「使用者取消」），畫面不再跳錯誤。
        }
        catch (Exception ex)
        {
            // 訊息是伺服器給的原文（額度用完、內容超過上限等，見 ExceptionAiFailure）。
            Logger.LogWarning(ex, "AI exception analysis failed. ExceptionLogId={ExceptionLogId}", item.Id);
            ShowAiFailure(ex is InvalidOperationException ? ex.Message : $"AI 分析未完成：{ex.Message}");
        }
    }

    /// <summary>串流的增量文字。</summary>
    private void OnAiDelta(string delta)
    {
        aiStreaming += delta;
        StateHasChanged();
    }

    private async Task RunElapsedTickerAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                aiElapsedSeconds++;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>關閉對話窗：分析中就取消（中斷請求，伺服器記 Cancelled）。</summary>
    private void OnAiModalCancel()
    {
        if (isAiRunning && aiCts is not null)
        {
            aiCts.Cancel();
            _ = MessageService.InfoAsync("已取消本次 AI 分析。");
        }

        aiModalVisible = false;
    }

    private void ShowAiFailure(string message)
    {
        // 取消後可能先收到別種錯誤，不要讓使用者關掉的視窗自己跳回來。
        if (aiCts?.IsCancellationRequested == true || isDisposed)
        {
            return;
        }

        aiErrorMessage = message;
        aiState = AiState.Failed;
        aiModalVisible = true;
    }

    private async Task OnAiCopyAsync()
    {
        if (await ClipboardInterop.CopyTextAsync(aiMarkdown))
        {
            _ = MessageService.SuccessAsync("已複製分析結果（Markdown 原文）。");
        }
        else
        {
            _ = MessageService.WarningAsync("瀏覽器不允許這次複製，請直接選取內容後按 Ctrl+C。");
        }
    }

    /// <summary>匯出 PDF：伺服器產生（附錄＝伺服器重組的送出內容、操作者＝呼叫者），稽核也由伺服器寫。</summary>
    private async Task OnAiExportPdfAsync()
    {
        if (isAiExporting || aiState != AiState.Succeeded || detailItem is null)
        {
            return;
        }

        isAiExporting = true;
        StateHasChanged();

        try
        {
            var download = await ExceptionLogApi.ExportAiPdfAsync(detailItem.Id, new DiagnosticAiPdfRequestDto { Markdown = aiMarkdown, Meta = aiMeta });
            await SaveDownloadAsync(download, "匯出 PDF 失敗");
        }
        finally
        {
            isAiExporting = false;
            StateHasChanged();
        }
    }

    public void Dispose()
    {
        isDisposed = true;
        aiCts?.Cancel();
    }
}
