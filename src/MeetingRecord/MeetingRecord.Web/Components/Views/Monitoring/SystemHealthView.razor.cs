using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Web.Components.Views.Monitoring;

/// <summary>
/// 系統健康監控（0.4.123 改版）。**管理者限定**、唯讀、不自動刷新。
///
/// <para>上半部是 17 項檢查（<see cref="ISystemHealthService"/>）；下半部「最近 24 小時警告以上日誌」
/// 預設收合、第一次展開才讀日誌檔（<see cref="ILogQueryService"/>），不拖慢開頁。</para>
///
/// <para>⚠️ 這頁每開一次、每按一次「重新檢查」都會跑一輪，所以任何檢查都不可呼叫付費 AI（開發慣例 §6.3）。</para>
/// </summary>
public partial class SystemHealthView : ComponentBase
{
    /// <summary>警告日誌區塊最多列幾筆（最新的）。更多請到「日誌檢視」。</summary>
    internal const int WarnLogTake = 200;

    [Inject]
    private ISystemHealthService SystemHealthService { get; set; } = default!;

    [Inject]
    private ILogQueryService LogQueryService { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<SystemHealthView> Logger { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private SystemHealthReport? report;
    private string? loadError;
    private bool isAccessChecked;
    private bool isLoading;

    private bool warnLogsExpanded;
    private bool warnLogsLoading;
    private LogQueryResult? warnLogs;
    private List<LogEntry> warnLogsDisplay = [];

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        // 0.4.120 起只限管理者：不看權限鍵（舊資料庫可能殘留「系統健康度」的勾選）。
        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("System health page denied because the current user is not an administrator.");
            return;
        }

        await RefreshAsync();
    }

    /// <summary>
    /// 重跑全部檢查；警告日誌區塊已展開的話一併重查。
    /// 開頭的早退不可省：兩次重疊會讓同一個 circuit 的 DbContext 被並行使用。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        loadError = null;
        StateHasChanged();

        try
        {
            report = await SystemHealthService.GetReportAsync();
        }
        catch (Exception ex)
        {
            loadError = $"讀取系統健康狀態失敗：{ex.GetType().Name}。";
            Logger.LogError(ex, "Loading system health report failed.");
        }
        finally
        {
            isLoading = false;
        }

        if (warnLogsExpanded)
        {
            await LoadWarnLogsAsync();
        }
    }

    private async Task ToggleWarnLogsAsync()
    {
        warnLogsExpanded = !warnLogsExpanded;
        if (warnLogsExpanded && warnLogs is null)
        {
            await LoadWarnLogsAsync();
        }
    }

    private async Task LoadWarnLogsAsync()
    {
        if (warnLogsLoading)
        {
            return;
        }

        warnLogsLoading = true;
        StateHasChanged();

        try
        {
            var (start, end) = LogTimeRanges.Last(TimeSpan.FromHours(24), DateTime.Now);
            warnLogs = await LogQueryService.QueryAsync(new LogQueryRequest
            {
                StartTime = start,
                EndTime = end,
                MinimumLevel = LogLevelRank.Warn,
                Take = WarnLogTake,
            });

            // 服務回傳時間升冪，畫面最新在前。
            warnLogsDisplay = warnLogs.Entries.Reverse().ToList();
        }
        catch (Exception ex)
        {
            warnLogs = new LogQueryResult { Message = $"讀取日誌失敗：{ex.GetType().Name}。" };
            warnLogsDisplay = [];
            Logger.LogError(ex, "Loading recent warning logs for system health failed.");
        }
        finally
        {
            warnLogsLoading = false;
            StateHasChanged();
        }
    }

    private IEnumerable<SystemHealthItem> ItemsOf(string group)
        => report?.Items.Where(item => item.Group == group) ?? [];

    private static string GetLightClass(SystemHealthLight light) => light switch
    {
        SystemHealthLight.Green => "health-ok",
        SystemHealthLight.Yellow => "health-warn",
        _ => "health-err",
    };

    private static string GetStatusText(SystemHealthStatus status) => status switch
    {
        SystemHealthStatus.Healthy => "正常",
        SystemHealthStatus.Degraded => "警示",
        _ => "異常",
    };

    private static string GetLevelClass(LogLevelRank rank) => rank switch
    {
        LogLevelRank.Fatal or LogLevelRank.Error => "log-level log-level-error",
        LogLevelRank.Warn => "log-level log-level-warn",
        _ => "log-level",
    };
}
