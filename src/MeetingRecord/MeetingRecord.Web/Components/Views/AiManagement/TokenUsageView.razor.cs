using System.Globalization;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Export;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Components.Views.AiManagement;

/// <summary>
/// Token 用量（1.0.30 由「AI 用量分析」改名並移到「系統管理 → AI 管理」）。**只限管理者**——
/// 這一頁看得到全公司的用量與人名，而且不套團隊過濾；1.0.29 以前可勾給角色，1.0.30 起權限鍵已退役。
///
/// <para>
/// 1.0.31 升級（參考 NET10-Blazor-Starter 的 /token-usage，畫面依本專案規範）：上限列、篩選、
/// 篩選範圍合計、頁籤（圖表／每日／依使用者／依功能／依模型／明細）、單筆明細（可連到 AI 對話紀錄）、
/// CSV／PDF 匯出、刪除與清除。上方的「本月概況」固定是全部功能、本月至今，不跟著篩選走。
/// </para>
///
/// <para>
/// 資料來自 0.4.80 起累積的用量帳本，**沒有回填**：畫面上一定要寫出統計起始日，
/// 否則使用者會以為系統在此之前都沒有花錢。
/// </para>
/// </summary>
public partial class TokenUsageView : ComponentBase
{
    private const string PdfContentType = "application/pdf";
    private const string TargetType = "AiUsageLog";

    /// <summary>上限列列出本月用量最高的前幾人。</summary>
    private const int TopUserCount = 5;

    private const string TabCharts = "charts";
    private const string TabDaily = "daily";
    private const string TabUser = "user";
    private const string TabFeature = "feature";
    private const string TabModel = "model";
    private const string TabDetail = "detail";

    /// <summary>CSV 欄位（1.0.31）。數字一律 InvariantCulture，金額不加千分位，試算表才算得動。</summary>
    public static readonly IReadOnlyList<CsvColumn<AiUsageDetail>> CsvColumns =
    [
        new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        new("功能", x => x.FeatureText),
        new("結果", x => x.OutcomeText),
        new("使用者", x => x.UserName),
        new("對象", x => x.TargetText),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("輸入token", x => x.InputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("輸出token", x => x.OutputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("快取token", x => x.CachedInputTokens?.ToString(CultureInfo.InvariantCulture)),
        new("音訊秒數", x => x.AudioSeconds?.ToString("0.###", CultureInfo.InvariantCulture)),
        new("時長推估", x => x.AudioSeconds is null ? null : x.IsAudioDurationEstimated ? "是" : "否"),
        new("估算金額", x => x.EstimatedCost?.ToString(CultureInfo.InvariantCulture)),
        new("幣別", x => x.Currency),
        new("匯率", x => x.ExchangeRate?.ToString(CultureInfo.InvariantCulture)),
        new("換算金額", x => x.ConvertedCost?.ToString(CultureInfo.InvariantCulture)),
        new("換算幣別", x => x.ConvertedCost is null ? null : x.ConvertedCurrency ?? x.Currency),
        new("錯誤訊息", x => x.ErrorMessage),
        new("呼叫識別碼", x => x.CallId?.ToString()),
    ];

    [Inject]
    private AiUsageAnalysisService AiUsageAnalysisService { get; set; } = default!;

    [Inject]
    private IAiQuotaService AiQuotaService { get; set; } = default!;

    [Inject]
    private AiCallLogService AiCallLogService { get; set; } = default!;

    [Inject]
    private IOptionsMonitor<AiQuotaSettings> QuotaOptions { get; set; } = default!;

    [Inject]
    private IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

    [Inject]
    private IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

    [Inject]
    private IPdfRenderer PdfRenderer { get; set; } = default!;

    [Inject]
    private FileDownloadInterop FileDownloadInterop { get; set; } = default!;

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
    private ILogger<TokenUsageView> Logger { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private bool isAccessChecked;
    private bool isLoading;
    private bool isExporting;

    // ───────── 本月概況與上限 ─────────
    private AiUsageSummary? summary;
    private AiQuotaStatus? globalQuota;
    private IReadOnlyList<AiQuotaUserUsage> topUsers = [];

    // ───────── 篩選 ─────────
    private DateTime? startDate;
    private DateTime? endDate;
    private string featureFilter = string.Empty;
    private string userFilter = string.Empty;
    private string modelFilter = string.Empty;
    private string outcomeFilter = string.Empty;
    private DateTime? purgeBefore;
    private AiUsageFilterOptions filterOptions = new([], []);

    /// <summary>
    /// 目前畫面上的結果所用的條件。按「查詢」才更新——只改下拉不按查詢時，翻頁與匯出仍要對得上畫面上的合計。
    /// </summary>
    private AiUsageFilter appliedFilter = new();

    // ───────── 篩選範圍 ─────────
    private AiUsageReport? report;
    private string activeTab = TabCharts;

    // ───────── 明細表 ─────────
    private IReadOnlyList<AiUsageRow> rows = [];
    private int pageIndex = 1;
    private int pageSize = MagicObjectHelper.PageSize;
    private int total;
    private bool sortAscending;

    // ───────── 明細窗 ─────────
    private bool detailVisible;
    private bool isDetailLoading;
    private AiUsageDetail? detail;

    /// <summary>同一次呼叫的 AI 對話紀錄 Id；null 表示沒有（舊紀錄、已清除或當時停用記錄）。</summary>
    private int? detailCallLogId;

    private int PerUserLimit => QuotaOptions.CurrentValue.PerUserMonthlyTwd;

    private string RetentionText => RetentionOptions.CurrentValue.AiUsageLogDays > 0
        ? $"用量紀錄保留 {RetentionOptions.CurrentValue.AiUsageLogDays} 天，每天自動清除（LogRetentionSettings:AiUsageLogDays）。"
        : "用量紀錄永久保留（LogRetentionSettings:AiUsageLogDays = 0）；需要時可在下方手動清除。";

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Token usage initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        // 1.0.30 起只限管理者：不再看權限鍵（舊資料庫可能殘留「AI 用量分析」的勾選）。判定前不讀任何資料。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Token usage denied because the current user is not an administrator.");
            return;
        }

        var today = DateTime.Today;
        startDate = new DateTime(today.Year, today.Month, 1);
        endDate = today;
        appliedFilter = BuildFilter();

        await LoadFilterOptionsAsync();
        await ReloadAsync();
    }

    // ───────── 載入 ─────────

    private async Task LoadFilterOptionsAsync()
    {
        try
        {
            filterOptions = await AiUsageAnalysisService.GetFilterOptionsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load token usage filter options.");
        }
    }

    private async Task OnSearchAsync()
    {
        if (startDate is { } start && endDate is { } end && start.Date > end.Date)
        {
            _ = MessageService.WarningAsync("開始日期不可晚於結束日期。");
            return;
        }

        appliedFilter = BuildFilter();
        pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await LoadFilterOptionsAsync();
        await ReloadAsync();
    }

    /// <summary>
    /// 重新載入整頁：本月概況、上限、篩選範圍統計與明細目前頁。
    /// 開頭的早退不可省：兩次載入重疊會讓同一個 circuit 的 DbContext 被並行使用。
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
            summary = await AiUsageAnalysisService.GetSummaryAsync();
            await LoadQuotaAsync();
            report = await AiUsageAnalysisService.GetReportAsync(appliedFilter);
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading token usage failed.");
            _ = MessageService.ErrorAsync($"載入 Token 用量失敗：{ex.Message}");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>上限列。讀不到時只記錯誤、不擋整頁（上限是護欄，不是這一頁的主體）。</summary>
    private async Task LoadQuotaAsync()
    {
        try
        {
            globalQuota = (await AiQuotaService.GetStatusAsync(null)).FirstOrDefault(x => x.Scope == AiQuotaScope.Global);
            topUsers = PerUserLimit > 0 ? await AiQuotaService.GetTopUsersAsync(TopUserCount) : [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI quota status failed.");
            globalQuota = null;
            topUsers = [];
        }
    }

    private async Task LoadRowsAsync()
    {
        var result = await AiUsageAnalysisService.GetRecentCallsAsync(
            new AiUsageQuery(appliedFilter, pageIndex, pageSize, sortAscending));
        rows = result.Rows;
        total = result.TotalCount;

        // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照 AI 對話紀錄）。
        if (rows.Count == 0 && total > 0 && pageIndex > 1)
        {
            pageIndex = Math.Max(1, (total + pageSize - 1) / pageSize);
            result = await AiUsageAnalysisService.GetRecentCallsAsync(
                new AiUsageQuery(appliedFilter, pageIndex, pageSize, sortAscending));
            rows = result.Rows;
            total = result.TotalCount;
        }
    }

    /// <summary>翻頁與排序。只重載明細，不重算整頁統計——統計與分頁無關。</summary>
    private async Task OnTableChangeAsync(QueryModel<AiUsageRow> queryModel)
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        try
        {
            pageIndex = queryModel.PageIndex;
            pageSize = queryModel.PageSize;
            var sort = queryModel.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
            sortAscending = sort?.SortDirection == SortDirection.Ascending;
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading token usage rows failed.");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private AiUsageFilter BuildFilter() => new(
        From: startDate?.Date,
        // 用「結束日隔天零時」當上界，結束日當天的紀錄才不會被切掉。
        ToExclusive: endDate?.Date.AddDays(1),
        Feature: Enum.TryParse<AiUsageFeature>(featureFilter, out var feature) ? feature : null,
        UserId: int.TryParse(userFilter, out var userId) ? userId : null,
        Model: string.IsNullOrWhiteSpace(modelFilter) ? null : modelFilter,
        Outcome: Enum.TryParse<AiUsageOutcome>(outcomeFilter, out var outcome) ? outcome : null);

    // ───────── 顯示輔助 ─────────

    private static string QuotaPillClass(double ratio, bool isReached)
        => isReached ? "token-pill token-pill-err" : ratio >= 0.8 ? "token-pill token-pill-warn" : string.Empty;

    private static string QuotaPillText(double ratio, bool isReached)
        => isReached ? "已達上限" : ratio >= 0.8 ? "已達 80%" : string.Empty;

    private double UserRatio(AiQuotaUserUsage user) => PerUserLimit > 0 ? (double)(user.Used / PerUserLimit) : 0d;

    private static string OutcomeClass(AiUsageOutcome outcome) => outcome switch
    {
        AiUsageOutcome.Succeeded => "token-pill",
        AiUsageOutcome.Cancelled => "token-pill token-pill-warn",
        _ => "token-pill token-pill-err",
    };

    /// <summary>每日金額長條的寬度（%）：以篩選範圍內最高的那一天為 100%。</summary>
    private int DailyBarWidth(AiUsageDailyRow row)
    {
        var max = report?.Daily.Count > 0 ? report.Daily.Max(x => x.Cost) : 0m;
        return max > 0 ? (int)Math.Round(row.Cost / max * 100m) : 0;
    }

    private string RangeText => appliedFilter.From is null && appliedFilter.ToExclusive is null
        ? "不限"
        : $"{appliedFilter.From?.ToString("yyyy/MM/dd") ?? "不限"}～{appliedFilter.ToExclusive?.AddDays(-1).ToString("yyyy/MM/dd") ?? "不限"}";

    /// <summary>報表表頭的篩選條件（區間以外）；沒有選的不列。</summary>
    private IReadOnlyList<(string Label, string Value)> DescribeFilters()
    {
        var list = new List<(string, string)>();
        if (appliedFilter.Feature is { } feature)
        {
            list.Add(("功能", AiUsageFeatureText.Describe(feature)));
        }

        if (appliedFilter.UserId is { } userId)
        {
            var name = filterOptions.Users.FirstOrDefault(x => x.UserId == userId).UserName;
            list.Add(("使用者", string.IsNullOrWhiteSpace(name) ? $"#{userId}" : name));
        }

        if (!string.IsNullOrWhiteSpace(appliedFilter.Model))
        {
            list.Add(("模型", appliedFilter.Model));
        }

        if (appliedFilter.Outcome is { } outcome)
        {
            list.Add(("結果", AiUsageOutcomeText.Describe(outcome)));
        }

        return list;
    }

    // ───────── 明細窗 ─────────

    private async Task OnViewAsync(AiUsageRow row)
    {
        if (isDetailLoading)
        {
            return;
        }

        isDetailLoading = true;
        detailVisible = true;
        detail = null;
        detailCallLogId = null;
        StateHasChanged();

        try
        {
            detail = await AiUsageAnalysisService.GetDetailAsync(row.Id);
            if (detail is null)
            {
                detailVisible = false;
                _ = MessageService.WarningAsync("找不到這筆用量紀錄，可能已被刪除。");
                return;
            }

            if (detail.CallId is { } callId)
            {
                detailCallLogId = await AiCallLogService.FindIdByCallIdAsync(callId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load token usage detail. Id={Id}", row.Id);
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

    private void OnOpenCallLog()
    {
        if (detail?.CallId is { } callId && detailCallLogId is not null)
        {
            NavigationManager.NavigateTo($"/ai-call-logs?callId={callId:D}");
        }
    }

    private string CallLogUnavailableText => detail?.CallId is null
        ? "1.0.29 之前的紀錄沒有對話可看。"
        : "對話紀錄已清除（超過保留天數或手動刪除），或當時停用了記錄。";

    private static string FormatPrice(decimal? value, string? currency, string unit)
        => value is { } price ? $"{currency} {price.ToString("0.######", CultureInfo.InvariantCulture)} / {unit}".Trim() : "—";

    // ───────── 匯出 ─────────

    private async Task OnExportCsvAsync()
    {
        if (isLoading || isExporting)
        {
            return;
        }

        isExporting = true;
        try
        {
            var items = await AiUsageAnalysisService.GetForExportAsync(appliedFilter);
            var bytes = CsvExport.ToCsv(CsvColumns, items);
            var fileName = $"MeetingRecord.Web-token-usage-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            await FileDownloadInterop.SaveBytesAsync(fileName, bytes, CsvExport.ContentType);

            await WriteAuditAsync(AuditActions.AiUsageExport, "*", $"format=csv; rows={items.Count}; range={RangeText}");
            NotifyDownloaded(fileName);
            if (items.Count >= AiUsageAnalysisService.MaxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {AiUsageAnalysisService.MaxExportRows:N0} 筆，請縮小查詢條件。");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Token usage CSV export failed.");
            _ = MessageService.ErrorAsync($"匯出失敗：{ex.Message}");
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    private async Task OnExportPdfAsync()
    {
        if (isLoading || isExporting || report is null)
        {
            return;
        }

        isExporting = true;
        StateHasChanged();

        try
        {
            var exportedAt = DateTime.Now;

            // 多拿一筆：用來判斷明細是否超過上限、要不要在報表加註。
            var details = await AiUsageAnalysisService.GetForExportAsync(appliedFilter, TokenUsageReportExporter.MaxDetailRows + 1);
            var html = TokenUsageReportExporter.BuildHtml(new TokenUsageReportRequest(
                SystemSettingsOptions.Value.SystemInformation.SystemVersion,
                CurrentUserService.CurrentUser.Account,
                exportedAt,
                RangeText,
                DescribeFilters(),
                report,
                details));

            var pdf = await PdfRenderer.RenderAsync(html);
            var fileName = TokenUsageReportExporter.BuildFileName(exportedAt);
            await FileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);

            await WriteAuditAsync(AuditActions.AiUsageExportPdf, "*", $"rows={report.CallCount}; range={RangeText}; bytes={pdf.Length}");
            NotifyDownloaded(fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Token usage PDF export failed.");
            _ = MessageService.ErrorAsync($"匯出 PDF 失敗：{ex.Message}");
        }
        finally
        {
            isExporting = false;
            StateHasChanged();
        }
    }

    // ───────── 刪除、清除 ─────────

    /// <summary>
    /// 確認窗的共同說明。⚠️ 本月的紀錄是 AI 用量上限的計算來源：刪掉會讓本月已用額度變少。
    /// </summary>
    private static string BuildImpactText(bool affectsCurrentMonth)
        => (affectsCurrentMonth ? "其中含本月的紀錄，本月 AI 用量上限的已用額度會跟著變少。" : string.Empty)
           + "AI 對話紀錄不會一起刪除；會議時數不受影響（儀表板讀的是會議自己的時長）。";

    private static DateTime CurrentMonthStart => new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private async Task OnDeleteAsync(AiUsageRow row)
    {
        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "刪除用量紀錄",
            Content = $"確定要刪除這一筆嗎？無法復原。{BuildImpactText(row.OccurredAt >= CurrentMonthStart)}",
            OkText = "刪除",
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
            if (await AiUsageAnalysisService.DeleteAsync(row.Id))
            {
                await WriteAuditAsync(AuditActions.AiUsageDelete, row.Id.ToString(), $"feature={row.Feature}; callId={row.CallId:N}");
                _ = MessageService.SuccessAsync("刪除成功");
            }
            else
            {
                _ = MessageService.WarningAsync("這筆紀錄已不存在。");
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete token usage row. Id={Id}", row.Id);
            _ = MessageService.ErrorAsync($"刪除失敗：{ex.Message}");
        }
    }

    private async Task OnPurgeAsync()
    {
        if (purgeBefore is null)
        {
            _ = MessageService.WarningAsync("請先選擇日期。");
            return;
        }

        if (isLoading || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var before = purgeBefore.Value.Date;
        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = $"清除 {before:yyyy-MM-dd} 之前的用量紀錄",
            Content = $"將刪除 {before:yyyy-MM-dd} 之前（不含當天）的所有用量紀錄，無法復原。{BuildImpactText(before > CurrentMonthStart)}",
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
            var removed = await AiUsageAnalysisService.PurgeBeforeAsync(before);
            await WriteAuditAsync(AuditActions.AiUsagePurge, "*", $"before={before:yyyy-MM-dd}; rows={removed}");
            _ = MessageService.SuccessAsync($"已清除 {removed:N0} 筆。");
            pageIndex = 1;
            await OnRefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge token usage rows.");
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
            Title = "清空全部用量紀錄",
            Content = $"將刪除所有用量紀錄，統計起始日會重新計算，無法復原。本月 AI 用量上限的已用額度會歸零。{BuildImpactText(false)}",
            OkText = "全部清除",
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
            var removed = await AiUsageAnalysisService.ClearAllAsync();
            await WriteAuditAsync(AuditActions.AiUsageClearAll, "*", $"rows={removed}");
            _ = MessageService.SuccessAsync($"已清空全部用量紀錄（{removed:N0} 筆）。");
            pageIndex = 1;
            await OnRefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear token usage rows.");
            _ = MessageService.ErrorAsync($"清除失敗：{ex.Message}");
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

    /// <summary>稽核只記編號、筆數與條件。</summary>
    private Task WriteAuditAsync(string action, string targetId, string detailText)
    {
        var currentUser = CurrentUserService.CurrentUser;
        return AuditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: currentUser.Id,
            actorAccount: currentUser.Account,
            targetType: TargetType,
            targetId: targetId,
            detail: detailText);
    }
}
