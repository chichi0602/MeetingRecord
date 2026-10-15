using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Components.Views.Monitoring;

/// <summary>
/// 日誌檢視（0.4.121，移植自 NET10-Blazor-Starter）。**管理者限定**。
///
/// <para>讀 NLog 的檔案目標（<see cref="ILogQueryService"/>），可篩選、展開堆疊、複製、匯出 .log，
/// 並可把查詢結果中最新 100 筆送給 AI 分析（會產生費用：先確認、邊生成邊顯示、關閉視窗即取消、寫入用量帳本）。</para>
/// </summary>
public partial class LogViewerView : ComponentBase, IDisposable
{
    private const string PdfContentType = "application/pdf";
    private const string LogContentType = "text/plain";

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

    [Inject]
    private ILogQueryService LogQueryService { get; set; } = default!;

    [Inject]
    private DiagnosticAiService DiagnosticAiService { get; set; } = default!;

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
    private ILogger<LogViewerView> Logger { get; set; } = default!;

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

    /// <summary>時間正序（舊→新）：匯出、複製、AI 都用這份。</summary>
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
    private AiPromptBuildResult? aiPrompt;
    private string aiStreaming = string.Empty;
    private string aiMarkdown = string.Empty;
    private string aiErrorMessage = string.Empty;
    private int aiElapsedSeconds;
    private List<(string Label, string Value)> aiMeta = [];

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
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 管理者限定：權限鍵刻意不在角色權限目錄裡，所以直接看 CheckIsAdmin。判定前不讀任何日誌。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/logs");
            Logger.LogWarning("Log viewer denied because the current user is not an administrator.");
            return;
        }

        aiAvailable = DiagnosticAiService.IsAvailable;
        aiUnavailableReason = DiagnosticAiService.UnavailableReason;

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

            var result = await LogQueryService.QueryAsync(request);

            entriesAscending = result.Entries.ToList();
            entriesDisplay = Enumerable.Reverse(entriesAscending).ToList();
            statusMessage = result.Message;
            warnings = result.Warnings;

            // 條件被夾住時把實際查詢的區間寫回選擇器，讓畫面與結果一致。
            startTime = result.AppliedStartTime;
            endTime = result.AppliedEndTime;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Log query failed.");
            statusMessage = $"查詢日誌失敗：{ex.GetType().Name}。";
            entriesAscending = [];
            entriesDisplay = [];
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

        await CopyAsync(BuildRawText(), entriesAscending.Count);
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

    private async Task OnExportAsync()
    {
        if (entriesAscending.Count == 0)
        {
            return;
        }

        try
        {
            var fileName = $"MeetingRecord.Web-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            await FileDownloadInterop.SaveBytesAsync(fileName, Utf8WithBom(BuildRawText()), LogContentType);

            Logger.LogInformation("Log export downloaded. Rows={Rows}", entriesAscending.Count);
            await WriteAuditAsync(AuditActions.LogViewerExport, success: true, $"format=log; rows={entriesAscending.Count}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Log export failed.");
            _ = MessageService.ErrorAsync($"匯出失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 匯出與「複製」共用：兩者內容必須完全一致，貼上的文字才能當成那份 .log 檔。
    /// 服務回傳的即為時間正序，直接沿用。
    /// </summary>
    private string BuildRawText() => string.Join(Environment.NewLine, entriesAscending.Select(entry => entry.Raw));

    /// <summary>UTF-8 含 BOM：Windows 記事本與 Excel 開中文才不會變亂碼。</summary>
    private static byte[] Utf8WithBom(string text)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
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
        if (isAiRunning || entriesAscending.Count == 0 || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        isAiRunning = true;
        try
        {
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

            await RunAiAnalysisAsync(prompt);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while running AI log analysis.");
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

    private async Task RunAiAnalysisAsync(AiPromptBuildResult prompt)
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
                AiUsageFeature.LogAnalysis,
                AiDiagnosticPrompts.LogAnalysis,
                prompt.UserMessage,
                OnAiDelta,
                token,
                relatedInfo: $"日誌 {startTime:yyyy-MM-dd HH:mm}～{endTime:yyyy-MM-dd HH:mm}，送出 {prompt.IncludedEntryCount}／{prompt.TotalEntryCount} 筆");

            if (isDisposed)
            {
                return;
            }

            aiMarkdown = result.Content;
            aiMeta = BuildAiMeta(prompt, result);
            aiState = AiState.Succeeded;

            await WriteAuditAsync(AuditActions.LogViewerAiAnalyze, success: true, BuildAiAuditDetail(prompt, result.ModelName, result.Elapsed));

            if (prompt.DroppedByEntryLimit)
            {
                _ = MessageService.WarningAsync($"查詢共 {prompt.TotalEntryCount:N0} 筆，僅分析最新 {prompt.IncludedEntryCount} 筆。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者關掉視窗：服務已把這次記成 Cancelled，畫面不再跳錯誤。
            Logger.LogInformation("AI log analysis cancelled by user.");
            await WriteAuditAsync(AuditActions.LogViewerAiAnalyze, success: false, BuildAiAuditDetail(prompt, null, null) + "；使用者取消");
        }
        catch (Exception ex)
        {
            if (ex is MeetingRecord.Business.Services.AiUsage.AiQuotaExceededException)
            {
                Logger.LogWarning("AI log analysis blocked by AI quota.");
            }
            else
            {
                Logger.LogError(ex, "AI log analysis failed. Entries={Entries}, Characters={Characters}", prompt.IncludedEntryCount, prompt.CharacterCount);
            }

            await WriteAuditAsync(AuditActions.LogViewerAiAnalyze, success: false, BuildAiAuditDetail(prompt, null, null) + $"；失敗：{ex.GetType().Name}");
            ShowAiFailure(DescribeFailure(ex));
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
        // 取消後供應商可能先丟出別種例外（連線被中斷），不要讓使用者關掉的視窗自己跳回來。
        if (aiCts?.IsCancellationRequested == true || isDisposed)
        {
            return;
        }

        aiErrorMessage = message;
        aiState = AiState.Failed;
        aiModalVisible = true;
    }

    /// <summary>把常見失敗翻成使用者看得懂、知道下一步的話。</summary>
    private static string DescribeFailure(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase))
        {
            return "送出的日誌量超過模型的內容上限。請縮小時間區間、提高最低等級或減少筆數後再試。";
        }

        return $"AI 分析未完成：{message}";
    }

    private List<(string Label, string Value)> BuildAiMeta(AiPromptBuildResult prompt, DiagnosticAiResult result)
    {
        var rows = new List<(string, string)>
        {
            ("查詢區間", $"{startTime:yyyy-MM-dd HH:mm:ss} ～ {endTime:yyyy-MM-dd HH:mm:ss}"),
            ("最低等級", string.IsNullOrEmpty(minimumLevel) ? "不限" : minimumLevel),
        };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            rows.Add(("關鍵字", keyword));
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            rows.Add(("TraceId", traceId));
        }

        rows.Add(("分析範圍", $"分析 {prompt.IncludedEntryCount} 筆／查詢 {prompt.TotalEntryCount} 筆"
            + (prompt.DroppedByEntryLimit ? "（只取最新的部分）" : string.Empty)));
        rows.Add(("使用模型", result.ModelName));
        if (result.Usage is { } usage)
        {
            rows.Add(("用量", $"輸入 {usage.InputTokens:N0}　輸出 {usage.OutputTokens:N0} tokens"));
        }

        rows.Add(("耗時", $"{result.Elapsed.TotalSeconds:F1} 秒"));
        return rows;
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
        if (isAiExporting || aiState != AiState.Succeeded || aiPrompt is null)
        {
            return;
        }

        isAiExporting = true;
        StateHasChanged();

        try
        {
            var exportedAt = DateTime.Now;
            var html = DiagnosticReportExporter.BuildHtml(
                "AI 日誌分析報告",
                [.. aiMeta, ("操作者", CurrentUserService.CurrentUser.Account)],
                aiMarkdown,
                "附錄：實際送出的日誌",
                aiPrompt.UserMessage,
                exportedAt);

            var pdf = await PdfRenderer.RenderAsync(html);
            var fileName = DiagnosticReportExporter.BuildFileName("AI日誌分析", exportedAt);
            await FileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);

            await WriteAuditAsync(AuditActions.LogViewerAiAnalyzeExportPdf, success: true, $"rows={aiPrompt.IncludedEntryCount}; bytes={pdf.Length}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI log analysis PDF export failed.");
            _ = MessageService.ErrorAsync($"匯出 PDF 失敗：{ex.Message}");
        }
        finally
        {
            isAiExporting = false;
            StateHasChanged();
        }
    }

    /// <summary>稽核只記筆數、字數與模型，不記日誌內容（日誌本身就可能含敏感資訊）。</summary>
    private string BuildAiAuditDetail(AiPromptBuildResult prompt, string? modelName, TimeSpan? elapsed)
        => $"送出 {prompt.IncludedEntryCount}/{prompt.TotalEntryCount} 筆、{prompt.CharacterCount} 字元"
            + (modelName is null ? string.Empty : $"；模型 {modelName}")
            + (elapsed is null ? string.Empty : $"；耗時 {(long)elapsed.Value.TotalMilliseconds} ms");

    private Task WriteAuditAsync(string action, bool success, string detail)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success,
            actorUserId: currentUser.Id,
            actorAccount: currentUser.Account,
            targetType: "LogQuery",
            targetId: $"{startTime:yyyyMMddHHmmss}-{endTime:yyyyMMddHHmmss}",
            detail: detail);
    }

    public void Dispose()
    {
        isDisposed = true;
        aiCts?.Cancel();
    }
}
