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
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Services;

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

    /// <summary>單一區塊顯示的字元上限；超過時改成純文字並截斷（完整內容請下載 JSON）。</summary>
    internal const int MaxDisplayCharacters = 200_000;

    /// <summary>CSV 欄位：只有中繼資料，**不含送出內容與回應**（那是逐字稿等敏感全文）。</summary>
    public static readonly IReadOnlyList<CsvColumn<AiCallLogAdapterModel>> CsvColumns =
    [
        new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("功能", x => x.FeatureText),
        new("結果", x => x.OutcomeText),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("使用者", x => x.UserName),
        new("會議Id", x => x.MeetingId?.ToString()),
        new("專案Id", x => x.ProjectId?.ToString()),
        new("關聯說明", x => x.RelatedInfo),
        new("HTTP", x => x.HttpStatus?.ToString()),
        new("耗時ms", x => x.ElapsedMilliseconds.ToString()),
        new("送出字元", x => x.RequestCharacters.ToString()),
        new("回應字元", x => x.ResponseCharacters.ToString()),
        new("錯誤", x => x.ErrorMessage),
        new("呼叫識別碼", x => x.CallId.ToString()),
    ];

    /// <summary>明細窗的檢視方式。</summary>
    internal enum ViewMode
    {
        Conversation,
        Request,
        Response,
    }

    /// <summary>明細窗裡的一個區塊。</summary>
    internal sealed record DisplayBlock(string Label, string Content, bool IsAssistant);

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
    private ViewMode viewMode = ViewMode.Conversation;
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

            viewMode = ViewMode.Conversation;
            renderMarkdown = BuildBlocks(detail, ViewMode.Conversation).All(x => x.Content.Length <= MaxDisplayCharacters);

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

    /// <summary>
    /// 依檢視方式組出要顯示的區塊。轉錄沒有訊息，送出內容改顯示檔名、時長與段落。
    /// 抽成 internal static 純函式以便單元測試。
    /// </summary>
    internal static IReadOnlyList<DisplayBlock> BuildBlocks(AiCallLogDetail detail, ViewMode mode)
    {
        var content = detail.Content;
        if (content is null)
        {
            return [];
        }

        var blocks = new List<DisplayBlock>();
        if (mode != ViewMode.Response)
        {
            if (content.Request.Kind == AiCallLogRequest.TranscriptionKind)
            {
                blocks.Add(new DisplayBlock("送出的音訊", DescribeTranscriptionRequest(content.Request), IsAssistant: false));
            }
            else
            {
                foreach (var message in content.Request.Messages)
                {
                    blocks.Add(new DisplayBlock(message.Role == "system" ? "系統指示" : "送出內容", message.Content, IsAssistant: false));
                }

                if (content.Request.Images.Count > 0)
                {
                    blocks.Add(new DisplayBlock(
                        "附帶圖片",
                        string.Join("\n", content.Request.Images.Select((x, i) => $"{i + 1}. {x.MediaType}，{FileSizeFormatter.Describe(x.Bytes)}（只記類型與大小，未保存圖片）")),
                        IsAssistant: false));
                }
            }
        }

        if (mode != ViewMode.Request)
        {
            var label = content.Response.IsPartial ? "回應（未完成，只有已收到的部分）" : "回應";
            blocks.Add(new DisplayBlock(label, string.IsNullOrEmpty(content.Response.Text) ? "（沒有內容）" : content.Response.Text, IsAssistant: true));
        }

        return blocks;
    }

    internal static string DescribeTranscriptionRequest(AiCallLogRequest request)
    {
        var builder = new StringBuilder();
        builder.Append($"檔案：{request.FileName}");
        if (request.SegmentIndex is { } index && request.SegmentCount is { } count)
        {
            builder.Append($"（第 {index}/{count} 段）");
        }

        if (request.AudioSeconds is { } seconds)
        {
            builder.Append($"\n時長：約 {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" + (request.IsAudioDurationEstimated ? "（推估）" : string.Empty));
        }

        builder.Append("\n（音訊本身不保存，只記錄檔名、時長與轉出的文字。）");
        return builder.ToString();
    }

    private IReadOnlyList<DisplayBlock> CurrentBlocks => detail is null ? [] : BuildBlocks(detail, viewMode);

    private static string Truncate(string text)
        => text.Length <= MaxDisplayCharacters
            ? text
            : text[..MaxDisplayCharacters] + $"\n\n……（內容共 {text.Length:N0} 字，只顯示前 {MaxDisplayCharacters:N0} 字；完整內容請下載 JSON）";

    private string UsageText
    {
        get
        {
            if (detail?.Usage is not { } usage)
            {
                return "用量帳本查不到同一次呼叫（帳本可能已被清除）。";
            }

            var amount = usage.EstimatedCost is { } cost
                ? usage.ExchangeRate is { } rate
                    ? $"約 {usage.ConvertedCurrency} {cost * rate:N2}（{usage.Currency} {cost:0.######}）"
                    : $"{usage.Currency} {cost:0.######}"
                : "未定價";

            var quantity = usage.AudioSeconds is { } seconds
                ? $"音訊 {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}" + (usage.IsAudioDurationEstimated ? "（推估）" : string.Empty)
                : usage.InputTokens is null
                    ? "供應商沒有回報用量"
                    : $"輸入 {usage.InputTokens:N0}／輸出 {usage.OutputTokens:N0} tokens"
                      + (usage.CachedInputTokens is > 0 ? $"（其中快取 {usage.CachedInputTokens:N0}）" : string.Empty);

            return $"{quantity}；{amount}";
        }
    }

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
                string.Join("\n\n", BuildBlocks(detail, ViewMode.Request).Select(x => $"【{x.Label}】\n{x.Content}")),
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
            var bytes = CsvExport.ToCsv(CsvColumns, items);
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
