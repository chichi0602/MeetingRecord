using System.Globalization;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.AiUsage;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.AiManagement;

/// <summary>
/// Token 用量（1.0.30 由「AI 用量分析」改名）。**只限管理者**——
/// 這一頁看得到全公司的用量與人名，而且不套團隊過濾。
///
/// <para>
/// 資料走 <c>api/v1/ai-usage</c>（1.1.36）：上限列、篩選、篩選範圍合計、頁籤（圖表／每日／依使用者／依功能／依模型／明細）、
/// 單筆明細（可連到 AI 對話紀錄）、CSV／PDF 匯出（伺服器產生）、刪除與清除。匯出、刪除、清除的稽核由伺服器寫。
/// 上方的「本月概況」固定是全部功能、本月至今，不跟著篩選走。
/// </para>
///
/// <para>
/// 資料來自 0.4.80 起累積的用量帳本，**沒有回填**：畫面上一定要寫出統計起始日，
/// 否則使用者會以為系統在此之前都沒有花錢。
/// </para>
/// </summary>
public partial class TokenUsagePage : ComponentBase
{
    private const string TabCharts = "charts";
    private const string TabDaily = "daily";
    private const string TabUser = "user";
    private const string TabFeature = "feature";
    private const string TabModel = "model";
    private const string TabDetail = "detail";

    [Inject] private AiUsageApi AiUsageApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private FileDownloadInterop FileDownloadInterop { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<TokenUsagePage> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private bool isAccessChecked;
    private bool isLoading;
    private bool isExporting;

    // ───────── 本月概況與上限 ─────────
    private AiUsageSummary? summary;
    private AiQuotaStatus? globalQuota;
    private IReadOnlyList<AiQuotaUserUsage> topUsers = [];
    private int perUserLimit;
    private int retentionDays;
    private int maxExportRows = AiUsageConstants.MaxExportRows;

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

    private int PerUserLimit => perUserLimit;

    private string RetentionText => retentionDays > 0
        ? $"用量紀錄保留 {retentionDays} 天，每天自動清除（LogRetentionSettings:AiUsageLogDays）。"
        : "用量紀錄永久保留（LogRetentionSettings:AiUsageLogDays = 0）；需要時可在下方手動清除。";

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        // 1.0.30 起只限管理者：不再看權限鍵（舊資料庫可能殘留「AI 用量分析」的勾選）。判定前不讀任何資料。API 也是 [AdminOnly]。
        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/token-usage");
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
        var result = await AiUsageApi.GetFilterOptionsAsync();
        if (result.Success && result.Data is { } options)
        {
            filterOptions = options;
        }
        else
        {
            Logger.LogWarning("Failed to load token usage filter options: {Message}", ApiClient.ErrorText(result));
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

    /// <summary>重新載入整頁：本月概況、上限、篩選範圍統計與明細目前頁。重疊時忽略。</summary>
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
            var overview = await AiUsageApi.GetOverviewAsync();
            if (!overview.Success || overview.Data is not { } data)
            {
                _ = MessageService.ErrorAsync($"載入 Token 用量失敗：{ApiClient.ErrorText(overview)}");
                return;
            }

            summary = data.Summary;
            globalQuota = data.GlobalQuota;
            topUsers = data.TopUsers;
            perUserLimit = data.PerUserLimit;
            retentionDays = data.RetentionDays;
            maxExportRows = data.MaxExportRows;

            var reportResult = await AiUsageApi.GetReportAsync(appliedFilter);
            if (!reportResult.Success)
            {
                _ = MessageService.ErrorAsync($"載入 Token 用量失敗：{ApiClient.ErrorText(reportResult)}");
                return;
            }

            report = reportResult.Data;
            await LoadRowsAsync();
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task LoadRowsAsync()
    {
        var result = await AiUsageApi.GetRowsAsync(new AiUsageQuery(appliedFilter, pageIndex, pageSize, sortAscending));
        rows = result.Data?.Rows ?? [];
        total = result.Data?.TotalCount ?? 0;

        // 停在最後一頁時刪掉該頁唯一一筆：夾回最後一頁重載（比照 AI 對話紀錄）。
        if (rows.Count == 0 && total > 0 && pageIndex > 1)
        {
            pageIndex = Math.Max(1, (total + pageSize - 1) / pageSize);
            result = await AiUsageApi.GetRowsAsync(new AiUsageQuery(appliedFilter, pageIndex, pageSize, sortAscending));
            rows = result.Data?.Rows ?? [];
            total = result.Data?.TotalCount ?? 0;
        }

        // 全選只算這一頁（1.0.53）：換頁、排序、篩選、刪除後都會走到這裡，勾選一律清掉。
        selectedRows = [];
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

    /// <summary>畫面上的篩選條件說明（區間以外）；沒有選的不列。PDF 報表表頭由伺服器用同一套規則組。</summary>
    private IReadOnlyList<(string Label, string Value)> DescribeFilters()
    {
        var list = new List<(string, string)>();
        if (appliedFilter.Feature is { } feature)
        {
            list.Add(("功能", AiUsageFeatureText.Describe(feature)));
        }

        if (appliedFilter.UserId is { } userId)
        {
            var name = filterOptions.Users.FirstOrDefault(x => x.UserId == userId)?.UserName;
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
            var result = await AiUsageApi.GetDetailAsync(row.Id);
            if (result.StatusCode == 404 || (result.Success && result.Data?.Detail is null))
            {
                detailVisible = false;
                _ = MessageService.WarningAsync("找不到這筆用量紀錄，可能已被刪除。");
                return;
            }

            if (!result.Success)
            {
                detailVisible = false;
                _ = MessageService.ErrorAsync($"讀取明細失敗：{ApiClient.ErrorText(result)}");
                return;
            }

            detail = result.Data!.Detail;
            detailCallLogId = result.Data.CallLogId;
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

    // ───────── 匯出（伺服器產生、伺服器寫稽核） ─────────

    private async Task OnExportCsvAsync()
    {
        if (isLoading || isExporting)
        {
            return;
        }

        isExporting = true;
        try
        {
            var download = await AiUsageApi.ExportCsvAsync(appliedFilter);
            if (!download.Success || download.Content is null)
            {
                _ = MessageService.ErrorAsync($"匯出失敗：{download.Error}");
                return;
            }

            await FileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
            NotifyDownloaded(download.FileName!);
            if (report?.CallCount >= maxExportRows)
            {
                _ = MessageService.WarningAsync($"只匯出最新 {maxExportRows:N0} 筆，請縮小查詢條件。");
            }
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
            var download = await AiUsageApi.ExportPdfAsync(appliedFilter);
            if (!download.Success || download.Content is null)
            {
                _ = MessageService.ErrorAsync($"匯出 PDF 失敗：{download.Error}");
                return;
            }

            await FileDownloadInterop.SaveBytesAsync(download.FileName!, download.Content, download.ContentType!);
            NotifyDownloaded(download.FileName!);
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
        if (isLoading)
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

        var result = await AiUsageApi.DeleteAsync(row.Id);
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
            _ = MessageService.ErrorAsync($"刪除失敗：{ApiClient.ErrorText(result)}");
        }

        await ReloadAsync();
    }

    #region 多選刪除（1.0.53）

    /// <summary>明細目前這一頁勾選的列；換頁、排序、篩選、重新載入時清掉（LoadRowsAsync）。</summary>
    private IEnumerable<AiUsageRow> selectedRows = [];

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
            Title = "刪除用量紀錄",
            Content = $"確定要刪除勾選的 {items.Count} 筆嗎？無法復原。{BuildImpactText(items.Any(x => x.OccurredAt >= CurrentMonthStart))}",
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
                var deleted = await AiUsageApi.DeleteAsync(x.Id);
                return deleted.Success ? null : deleted.StatusCode == 404 ? "找不到這筆紀錄" : ApiClient.ErrorText(deleted);
            });
            Logger.LogInformation("Batch token usage delete completed. Succeeded={Succeeded}, Failed={Failed}", result.Succeeded, result.Failures.Count);
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

        var result = await AiUsageApi.PurgeAsync(before);
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync($"清除失敗：{ApiClient.ErrorText(result)}");
            return;
        }

        _ = MessageService.SuccessAsync($"已清除 {result.Data:N0} 筆。");
        pageIndex = 1;
        await OnRefreshAsync();
    }

    private async Task OnClearAllAsync()
    {
        if (isLoading)
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

        var result = await AiUsageApi.ClearAllAsync();
        if (!result.Success)
        {
            _ = MessageService.ErrorAsync($"清除失敗：{ApiClient.ErrorText(result)}");
            return;
        }

        _ = MessageService.SuccessAsync($"已清空全部用量紀錄（{result.Data:N0} 筆）。");
        pageIndex = 1;
        await OnRefreshAsync();
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
