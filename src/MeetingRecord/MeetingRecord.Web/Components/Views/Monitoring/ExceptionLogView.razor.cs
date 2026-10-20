using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Components.Views.Monitoring;

/// <summary>
/// 系統例外紀錄（0.4.122，移植自 NET10-Blazor-Starter，不含 Email 告警與 AI 追問）。**管理者限定**。
///
/// <para>資料由 <see cref="ExceptionLogProvider"/> → 背景寫入器自動收集；本頁負責查詢、明細、複製、匯出 CSV、
/// 清除與 AI 例外分析（會產生費用：先確認、邊生成邊顯示、關閉視窗即取消、寫入用量帳本）。</para>
/// </summary>
public partial class ExceptionLogView : ComponentBase, IDisposable
{
    private const string PdfContentType = "application/pdf";

    /// <summary>複製全部（每筆含完整堆疊）超過這個筆數先確認。</summary>
    private const int CopyAllConfirmThreshold = 100;

    /// <summary>CSV 欄位（含帳號：這份是管理者自己下載的，不會送給 AI）。</summary>
    public static readonly IReadOnlyList<CsvColumn<ExceptionLogAdapterModel>> CsvColumns =
    [
        new("最後發生", x => x.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("次數", x => x.OccurrenceCount.ToString()),
        new("例外類型", x => x.ExceptionType),
        new("訊息", x => x.Message),
        new("來源", x => x.Source),
        new("頁面", x => x.Page),
        new("操作", x => x.Operation),
        new("記錄器", x => x.LoggerName),
        new("使用者", x => x.Account),
        new("首次發生", x => x.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("最後追蹤碼", x => x.LastTraceId),
    ];

    private enum AiState
    {
        None,
        Running,
        Failed,
        Succeeded,
    }

    [Inject]
    private ExceptionLogService ExceptionLogService { get; set; } = default!;

    [Inject]
    private DiagnosticAiService DiagnosticAiService { get; set; } = default!;

    [Inject]
    private IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

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
    private ILogger<ExceptionLogView> Logger { get; set; } = default!;

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

    private bool detailVisible;
    private ExceptionLogAdapterModel? detailItem;
    private string detailStackTrace = string.Empty;
    private string? detailRawStackTrace;
    private bool isStackTraceLoading;

    // ───────── AI 分析 ─────────
    private bool aiAvailable;
    private string aiUnavailableReason = string.Empty;
    private bool aiModalVisible;
    private AiState aiState = AiState.None;
    private bool isAiRunning;
    private bool isAiExporting;
    private CancellationTokenSource? aiCts;
    private string aiPrompt = string.Empty;
    private string aiStreaming = string.Empty;
    private string aiMarkdown = string.Empty;
    private string aiErrorMessage = string.Empty;
    private int aiElapsedSeconds;
    private List<(string Label, string Value)> aiMeta = [];

    private int PurgeDays => RetentionOptions.CurrentValue.ManualExceptionLogDays;

    private string RetentionText => RetentionOptions.CurrentValue.ExceptionLogDays > 0
        ? $"「最後發生」超過 {RetentionOptions.CurrentValue.ExceptionLogDays} 天的紀錄每天自動清除。"
        : "自動清除已停用（LogRetentionSettings:ExceptionLogDays = 0）。";

    private string AiButtonTitle => aiAvailable
        ? "把這筆例外（不含帳號）送給 AI 分析根因與修正建議（會產生費用）"
        : aiUnavailableReason;

    private string AiModalTitle => aiState switch
    {
        AiState.Running => "AI 例外分析（進行中）",
        AiState.Failed => "AI 例外分析（未完成）",
        _ => "AI 例外分析結果",
    };

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡，所以直接看 CheckIsAdmin。判定前不讀任何資料。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/system-exceptions");
            Logger.LogWarning("Exception log view denied because the current user is not an administrator.");
            return;
        }

        aiAvailable = DiagnosticAiService.IsAvailable;
        aiUnavailableReason = DiagnosticAiService.UnavailableReason;

        await ReloadAsync();
    }

    // ───────── 查詢 ─────────

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
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
            var result = await ExceptionLogService.GetAsync(BuildQuery(_pageIndex, _pageSize));
            rows = result.Result.ToList();
            _total = result.Count;

            // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照 TodoViewView）。
            // ⚠️ Math.Max(1, ...) 不可省：PageIndex < 1 時 AntDesign 不會再觸發 OnChange。
            if (rows.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                result = await ExceptionLogService.GetAsync(BuildQuery(_pageIndex, _pageSize));
                rows = result.Result.ToList();
                _total = result.Count;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load exception logs.");
            _ = MessageService.ErrorAsync($"載入例外紀錄失敗：{ex.Message}");
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
    private ExceptionLogQuery BuildFullQuery() => BuildQuery(1, ExceptionLogService.MaxRows);

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
        isStackTraceLoading = true;
        detailVisible = true;
        StateHasChanged();

        try
        {
            var stackTrace = await ExceptionLogService.GetStackTraceAsync(item.Id);
            detailRawStackTrace = string.IsNullOrWhiteSpace(stackTrace) ? null : stackTrace;
            detailStackTrace = detailRawStackTrace
                ?? "堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。完整日誌請到「日誌檢視」以最後追蹤碼查詢。";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to read exception stack trace. ExceptionLogId={ExceptionLogId}", item.Id);
            detailStackTrace = $"讀取堆疊失敗：{ex.Message}";
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

        try
        {
            var stackTrace = await ExceptionLogService.GetStackTraceAsync(row.Data.Id);
            await CopyAsync(ExceptionLogClipboardText.Build(row.Data, stackTrace), 1);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to copy exception log. ExceptionLogId={ExceptionLogId}", row.Data.Id);
            _ = MessageService.ErrorAsync("複製失敗，請改按「查看」再複製。");
        }
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
            var items = (await ExceptionLogService.GetAsync(BuildFullQuery())).Result.ToList();
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

            var stackTraces = await ExceptionLogService.GetStackTracesAsync(items.Select(x => x.Id).ToList());
            await CopyAsync(ExceptionLogClipboardText.BuildMany(items.Select(x => (x, stackTraces[x.Id]))), items.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to copy exception logs.");
            _ = MessageService.ErrorAsync($"複製失敗：{ex.Message}");
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
            var result = await ExceptionLogService.GetAsync(BuildFullQuery());
            var bytes = CsvExport.ToCsv(CsvColumns, result.Result);
            var fileName = $"MeetingRecord.Web-exceptions-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            await FileDownloadInterop.SaveBytesAsync(fileName, bytes, CsvExport.ContentType);

            await WriteAuditAsync(AuditActions.ExceptionLogExport, "*", $"format=csv; rows={result.Count}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Exception log export failed.");
            _ = MessageService.ErrorAsync($"匯出失敗：{ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnDeleteAsync(ExceptionLogAdapterModel item)
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
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

        var result = await ExceptionLogService.DeleteAsync(item.Id);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLogDelete, item.Id.ToString(), $"type={item.ShortExceptionType}");
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
        if (items.Count == 0 || isBatchDeleting || isLoading || !AuthenticationStateHelper.CheckIsAdmin())
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
            // 比照單筆：每刪成一列就記一筆稽核。
            var result = await BatchRunner.RunAsync(items, x => x.ShortExceptionType, async x =>
            {
                var r = await ExceptionLogService.DeleteAsync(x.Id);
                if (!r.Success)
                {
                    return r.Message;
                }

                await WriteAuditAsync(AuditActions.ExceptionLogDelete, x.Id.ToString(), $"type={x.ShortExceptionType}");
                return null;
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
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
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

        var result = await ExceptionLogService.PurgeAsync(days);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLogPurge, "*", $"days={days}; {result.Message}");
            _ = MessageService.SuccessAsync(string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
            await ReloadAsync();
        }
        else
        {
            _ = MessageService.ErrorAsync(result.Message);
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

        var result = await ExceptionLogService.ClearAllAsync();
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLogClearAll, "*", "清空全部例外紀錄");
            _ = MessageService.SuccessAsync("已清空全部例外紀錄");
            _pageIndex = 1;
            await ReloadAsync();
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

    // ───────── AI 例外分析（單次）─────────

    private async Task OnAiAnalyzeAsync()
    {
        if (isAiRunning || detailItem is null || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        isAiRunning = true;
        var item = detailItem;
        try
        {
            var prompt = AiExceptionPromptBuilder.Build(item, detailRawStackTrace);

            var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認執行 AI 例外分析（會產生費用）",
                Content = $"將把這筆例外的類型、訊息、頁面、操作與完整堆疊（約 {prompt.Length:N0} 字，不含帳號）送到 Azure OpenAI 分析，會產生費用。確定要繼續嗎？",
                OkText = "開始分析",
                CancelText = "取消",
                MaskClosable = false,
            });

            if (!confirmed)
            {
                return;
            }

            await RunAiAnalysisAsync(item, prompt);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while running AI exception analysis.");
            ShowAiFailure($"AI 分析發生未預期的錯誤：{ex.Message}");
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

    private async Task RunAiAnalysisAsync(ExceptionLogAdapterModel item, string prompt)
    {
        aiPrompt = prompt;
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
            var result = await DiagnosticAiService.AnalyzeAsync(
                AiUsageFeature.ExceptionAnalysis,
                AiDiagnosticPrompts.ExceptionAnalysis,
                prompt,
                OnAiDelta,
                token,
                relatedInfo: $"例外紀錄 #{item.Id}（{item.ExceptionType}）");

            if (isDisposed)
            {
                return;
            }

            aiMarkdown = result.Content;
            aiMeta =
            [
                ("例外類型", item.ExceptionType),
                ("累計次數", item.OccurrenceCount.ToString("N0")),
                ("最後發生", item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("使用模型", result.ModelName),
            ];
            if (result.Usage is { } usage)
            {
                aiMeta.Add(("用量", $"輸入 {usage.InputTokens:N0}　輸出 {usage.OutputTokens:N0} tokens"));
            }

            aiMeta.Add(("耗時", $"{result.Elapsed.TotalSeconds:F1} 秒"));
            aiState = AiState.Succeeded;

            await WriteAuditAsync(AuditActions.ExceptionLogAiAnalyze, item.Id.ToString(),
                $"chars={prompt.Length}; model={result.ModelName}; elapsedMs={(long)result.Elapsed.TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者關掉視窗：服務已記成 Cancelled，畫面不再跳錯誤。
            await WriteAuditAsync(AuditActions.ExceptionLogAiAnalyze, item.Id.ToString(), $"chars={prompt.Length}; 使用者取消", success: false);
        }
        catch (Exception ex)
        {
            if (ex is MeetingRecord.Business.Services.AiUsage.AiQuotaExceededException)
            {
                Logger.LogWarning("AI exception analysis blocked by AI quota. ExceptionLogId={ExceptionLogId}", item.Id);
            }
            else
            {
                Logger.LogError(ex, "AI exception analysis failed. ExceptionLogId={ExceptionLogId}", item.Id);
            }

            await WriteAuditAsync(AuditActions.ExceptionLogAiAnalyze, item.Id.ToString(), $"chars={prompt.Length}; 失敗：{ex.GetType().Name}", success: false);
            ShowAiFailure(ex.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
                ? "這筆例外的內容（多半是堆疊）超過模型的內容上限，無法送交 AI 分析。"
                : $"AI 分析未完成：{ex.Message}");
        }
    }

    /// <summary>串流的增量文字；從背景執行緒呼叫，切回 circuit 再重繪。</summary>
    private void OnAiDelta(string delta)
        => _ = InvokeAsync(() =>
        {
            aiStreaming += delta;
            StateHasChanged();
        });

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

    /// <summary>關閉對話窗：分析中就取消（服務記 Cancelled）。</summary>
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
        // 取消後供應商可能先丟出別種例外，不要讓使用者關掉的視窗自己跳回來。
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

    private async Task OnAiExportPdfAsync()
    {
        if (isAiExporting || aiState != AiState.Succeeded)
        {
            return;
        }

        isAiExporting = true;
        StateHasChanged();

        try
        {
            var exportedAt = DateTime.Now;
            var html = DiagnosticReportExporter.BuildHtml(
                "AI 例外分析報告",
                [.. aiMeta, ("操作者", CurrentUserService.CurrentUser.Account)],
                aiMarkdown,
                "附錄：實際送出的例外內容",
                aiPrompt,
                exportedAt);

            var pdf = await PdfRenderer.RenderAsync(html);
            var fileName = DiagnosticReportExporter.BuildFileName("AI例外分析", exportedAt);
            await FileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);

            await WriteAuditAsync(AuditActions.ExceptionLogAiAnalyzeExportPdf, detailItem?.Id.ToString() ?? "*", $"bytes={pdf.Length}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI exception analysis PDF export failed.");
            _ = MessageService.ErrorAsync($"匯出 PDF 失敗：{ex.Message}");
        }
        finally
        {
            isAiExporting = false;
            StateHasChanged();
        }
    }

    /// <summary>稽核不記例外內容（可能含敏感資訊），只記編號、字數、模型與耗時。</summary>
    private Task WriteAuditAsync(string action, string targetId, string detail, bool success = true)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success,
            actorUserId: currentUser.Id,
            actorAccount: currentUser.Account,
            targetType: "ExceptionLog",
            targetId: targetId,
            detail: detail);
    }

    public void Dispose()
    {
        isDisposed = true;
        aiCts?.Cancel();
    }
}
