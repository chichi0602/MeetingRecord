using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.Monitoring;

/// <summary>
/// 日誌檢視（0.4.121，移植自 NET10-Blazor-Starter）。**管理者限定**。
///
/// <para>讀 NLog 的檔案目標（<c>api/v1/monitoring/logs/query</c>），可篩選、展開堆疊、複製、匯出 .log，
/// 並可把查詢結果中最新 100 筆送給 AI 分析（會產生費用：先確認、邊生成邊顯示、關閉視窗即取消、寫入用量帳本）。
/// 匯出與 AI 分析都由伺服器依同一組條件重查，不收這裡的日誌；稽核由伺服器寫。</para>
/// </summary>
public partial class LogViewerPage : ComponentBase, IDisposable
{
    /// <summary>複製全部超過這個筆數先確認——內容太大時貼上會很慢。</summary>
    private const int CopyAllConfirmThreshold = 1000;

    private static readonly (string Key, string Label)[] LevelOptions =
    [
        (string.Empty, "不限等級"),
        ("TRACE", "TRACE 以上"),
        ("DEBUG", "DEBUG 以上"),
        ("INFO", "INFO 以上"),
        ("WARN", "WARN 以上"),
        ("ERROR", "ERROR 以上"),
        ("FATAL", "FATAL"),
    ];

    private enum AiState
    {
        None,
        Running,
        Failed,
        Succeeded,
    }

    [Inject] private MonitoringApi MonitoringApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private FileDownloadInterop FileDownloadInterop { get; set; } = default!;
    [Inject] private ClipboardInterop ClipboardInterop { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private ILogger<LogViewerPage> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private Table<LogEntry>? logTable;
    private bool isAccessChecked;
    private bool isLoading;
    private bool isDisposed;

    private DateTime? startTime;
    private DateTime? endTime;
    private string minimumLevel = string.Empty;
    private int takeCount = LogQueryRequest.DefaultTake;
    private string keyword = string.Empty;
    private string traceId = string.Empty;

    /// <summary>目前畫面上的結果所用的條件（已夾回實際查詢的區間）。匯出與 AI 分析帶這一組，伺服器重查。</summary>
    private LogQueryRequest? appliedQuery;

    /// <summary>時間正序（舊→新）：複製、AI 確認窗的筆數與字數都用這份。</summary>
    private List<LogEntry> entriesAscending = [];

    /// <summary>畫面顯示用：最新在前。</summary>
    private List<LogEntry> entriesDisplay = [];

    private string statusMessage = string.Empty;
    private List<string> warnings = [];

    // ───────── AI 分析 ─────────
    private bool aiAvailable;
    private string aiUnavailableReason = string.Empty;
    private bool aiModalVisible;
    private AiState aiState = AiState.None;
    private bool isAiRunning;
    private bool isAiExporting;
    private CancellationTokenSource? aiCts;
    private LogQueryRequest? aiQuery;
    private AiPromptBuildResult? aiPrompt;
    private string aiStreaming = string.Empty;
    private string aiMarkdown = string.Empty;
    private string aiErrorMessage = string.Empty;
    private int aiElapsedSeconds;
    private List<DiagnosticAiMetaLineDto> aiMeta = [];

    private string AiButtonTitle => aiAvailable
        ? $"把本次查詢結果中最新 {AiLogPromptBuilder.MaxEntries} 筆送給 AI 整理（會產生費用）"
        : aiUnavailableReason;

    private string AiModalTitle => aiState switch
    {
        AiState.Running => "AI 日誌分析（進行中）",
        AiState.Failed => "AI 日誌分析（未完成）",
        _ => "AI 日誌分析結果",
    };

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡。判定前不讀任何日誌。API 也是 [AdminOnly]。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/logs");
            Logger.LogWarning("Log viewer denied because the current user is not an administrator.");
            return;
        }

        var meta = await MonitoringApi.GetLogViewerMetaAsync();
        if (meta.Success && meta.Data is { } data)
        {
            aiAvailable = data.AiAvailable;
            aiUnavailableReason = data.AiUnavailableReason;
        }
        else
        {
            aiUnavailableReason = $"無法確認 AI 設定：{ApiClient.ErrorText(meta)}";
        }

        endTime = DateTime.Now;
        startTime = endTime.Value.AddHours(-1);

        await OnQueryAsync();
    }

    // ───────── 查詢 ─────────

    /// <summary>快速區間：只改時間並立即查詢，等級、筆數、關鍵字、TraceId 維持原值。</summary>
    private async Task OnQuickRangeAsync((DateTime Start, DateTime End) range)
    {
        (startTime, endTime) = range;
        await OnQueryAsync();
    }

    /// <summary>
    /// 查前後 1 分鐘：要看這筆前後「所有」發生的事，所以清掉等級、關鍵字與 TraceId。
    /// 筆數拉到上限：服務只保留最新 N 筆，筆數不夠時較早的紀錄（可能就是這筆本身）會被切掉。
    /// </summary>
    private async Task OnAroundEntryAsync(LogEntry entry)
    {
        (startTime, endTime) = LogTimeRanges.Around(entry.Timestamp);
        minimumLevel = string.Empty;
        keyword = string.Empty;
        traceId = string.Empty;
        takeCount = LogQueryRequest.MaxTake;
        await OnQueryAsync();
    }

    private async Task OnQueryAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;

        // 重新查詢一律回到收合。⚠️ 只靠 DataSource 換掉不夠：AntDesign 依 RowKey 記住每列的展開狀態，
        // 新結果的 Sequence 與舊結果重疊時，那些列會沿用舊的展開狀態。
        logTable?.CollapseAll();
        statusMessage = string.Empty;
        warnings = [];
        StateHasChanged();

        try
        {
            var request = new LogQueryRequest
            {
                StartTime = startTime ?? DateTime.Now.AddHours(-1),
                EndTime = endTime ?? DateTime.Now,
                Take = takeCount,
                MinimumLevel = LogLevelRankHelper.FromLevelText(minimumLevel, LogLevelRank.Any),
                Keyword = keyword ?? string.Empty,
                TraceId = traceId ?? string.Empty,
            };

            var response = await MonitoringApi.QueryLogsAsync(request);
            if (!response.Success || response.Data is not { } result)
            {
                statusMessage = $"查詢日誌失敗：{ApiClient.ErrorText(response)}";
                entriesAscending = [];
                entriesDisplay = [];
                appliedQuery = null;
                return;
            }

            entriesAscending = result.Entries.ToList();
            entriesDisplay = Enumerable.Reverse(entriesAscending).ToList();
            statusMessage = result.Message;
            warnings = result.Warnings;

            // 條件被夾住時把實際查詢的區間寫回選擇器，讓畫面與結果一致。
            startTime = result.AppliedStartTime;
            endTime = result.AppliedEndTime;
            request.StartTime = result.AppliedStartTime;
            request.EndTime = result.AppliedEndTime;
            appliedQuery = request;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private void OnExpandAll() => logTable?.ExpandAll();

    private void OnCollapseAll() => logTable?.CollapseAll();

    private static string GetLevelClass(LogLevelRank rank) => rank switch
    {
        LogLevelRank.Fatal or LogLevelRank.Error => "log-level log-level-error",
        LogLevelRank.Warn => "log-level log-level-warn",
        LogLevelRank.Info => "log-level log-level-info",
        _ => "log-level",
    };

    // ───────── 複製與匯出 ─────────

    /// <summary>
    /// 點一下任一列即複製該筆原文。只複製、不切換展開——展開仍靠左邊的展開圖示。
    /// </summary>
    private Task OnRowClickAsync(RowData<LogEntry> row) => CopyAsync(row.Data.Raw, 1);

    private async Task OnCopyAllAsync()
    {
        if (entriesAscending.Count == 0)
        {
            return;
        }

        if (entriesAscending.Count > CopyAllConfirmThreshold)
        {
            var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認複製",
                Content = $"本次查詢共 {entriesAscending.Count:N0} 筆，內容較大，貼上時可能較慢；大量資料建議改用「匯出」。",
                OkText = "仍要複製",
                CancelText = "取消",
                MaskClosable = false,
            });

            if (!confirmed)
            {
                return;
            }
        }

        // 與伺服器匯出的 .log 內容相同：時間正序、以換行串接原文。
        await CopyAsync(string.Join(Environment.NewLine, entriesAscending.Select(entry => entry.Raw)), entriesAscending.Count);
    }

    private async Task CopyAsync(string text, int rows)
    {
        if (await ClipboardInterop.CopyTextAsync(text))
        {
            _ = MessageService.SuccessAsync($"已複製 {rows:N0} 筆日誌到剪貼簿。");
        }
        else
        {
            _ = MessageService.WarningAsync("瀏覽器不允許這次複製，請展開後直接選取內容再按 Ctrl+C。");
        }
    }

    /// <summary>匯出 .log：伺服器依同一組條件重查產生（UTF-8 含 BOM），稽核由伺服器寫。</summary>
    private async Task OnExportAsync()
    {
        if (entriesAscending.Count == 0 || appliedQuery is null)
        {
            return;
        }

        await SaveDownloadAsync(await MonitoringApi.ExportLogsAsync(appliedQuery), "匯出失敗");
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

    // ───────── AI 分析 ─────────

    private async Task OnAiAnalyzeAsync()
    {
        // 早退防連點：確認窗還開著時再按一次，不能疊出第二個確認窗或第二次呼叫。
        if (isAiRunning || entriesAscending.Count == 0 || appliedQuery is null)
        {
            return;
        }

        isAiRunning = true;
        try
        {
            // 確認窗的筆數與字數用畫面上的結果算（同一套組字）；實際送出的內容由伺服器依同一組條件重查重組。
            var prompt = AiLogPromptBuilder.Build(entriesAscending);
            if (prompt.IsEmpty)
            {
                return;
            }

            var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "確認執行 AI 日誌分析（會產生費用）",
                Content = $"將把本次查詢結果中最新 {prompt.IncludedEntryCount} 筆日誌的原文（約 {prompt.CharacterCount:N0} 字，"
                    + "可能含帳號、檔名與使用者輸入的內容）送到 Azure OpenAI 分析，會產生費用。"
                    + (prompt.DroppedByEntryLimit ? $"查詢共 {prompt.TotalEntryCount:N0} 筆，只會分析最新 {prompt.IncludedEntryCount} 筆。" : string.Empty)
                    + "確定要繼續嗎？",
                OkText = "開始分析",
                CancelText = "取消",
                MaskClosable = false,
            });

            if (!confirmed)
            {
                return;
            }

            await RunAiAnalysisAsync(appliedQuery, prompt);
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

    /// <summary>送出由伺服器依查詢條件重查組成；成功、失敗、取消的稽核都由伺服器寫。</summary>
    private async Task RunAiAnalysisAsync(LogQueryRequest query, AiPromptBuildResult prompt)
    {
        aiQuery = query;
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
            var done = await MonitoringApi.AnalyzeLogsAsync(query, OnAiDelta, token);
            if (isDisposed)
            {
                return;
            }

            aiMarkdown = done.Content ?? string.Empty;
            aiMeta = done.Meta ?? [];
            aiState = AiState.Succeeded;

            if (prompt.DroppedByEntryLimit)
            {
                _ = MessageService.WarningAsync($"查詢共 {prompt.TotalEntryCount:N0} 筆，僅分析最新 {prompt.IncludedEntryCount} 筆。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者關掉視窗：請求中斷，伺服器停止呼叫 AI（帳本記成 Cancelled、稽核記「使用者取消」），畫面不再跳錯誤。
            Logger.LogInformation("AI log analysis cancelled by user.");
        }
        catch (Exception ex)
        {
            // 訊息是伺服器給的原文（額度用完、日誌量超過上限等，見 LogAiFailure）。
            Logger.LogWarning(ex, "AI log analysis failed.");
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
        // 取消後可能先收到別種錯誤（連線被中斷），不要讓使用者關掉的視窗自己跳回來。
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

    /// <summary>匯出 PDF：伺服器產生（附錄＝伺服器重查重組的送出內容、操作者＝呼叫者），稽核也由伺服器寫。</summary>
    private async Task OnAiExportPdfAsync()
    {
        if (isAiExporting || aiState != AiState.Succeeded || aiQuery is null)
        {
            return;
        }

        isAiExporting = true;
        StateHasChanged();

        try
        {
            var download = await MonitoringApi.ExportLogAiPdfAsync(new LogAiPdfRequestDto { Query = aiQuery, Markdown = aiMarkdown, Meta = aiMeta });
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
