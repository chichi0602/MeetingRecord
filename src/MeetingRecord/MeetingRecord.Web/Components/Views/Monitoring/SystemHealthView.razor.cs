using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Diagnostics;
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
/// <para>⚠️ 這頁每開一次、每按一次「重新檢查」都會跑一輪，所以任何檢查都不可呼叫付費 AI（開發慣例 §6.3）。
/// 唯一的例外是管理者手動按的「測試 AI 連線」（1.0.35）：先跳「（會產生費用）」確認窗，結果只顯示、不併入分數。</para>
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

    [Inject]
    private AiConnectionTestService AiConnectionTestService { get; set; } = default!;

    [Inject]
    private IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    /// <summary>「AI 供應商設定」那一項的 Category（<c>SystemHealthService.CheckAiProvider</c>），測試結果顯示在它底下。</summary>
    private const string AiProviderCategory = "AiProvider";

    // ───────── 測試 AI 連線（1.0.35） ─────────
    private bool isTestingAi;
    private IReadOnlyList<AiConnectionProbeResult>? aiProbeResults;
    private DateTime? aiProbeTestedAt;

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
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/system-health");
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

    /// <summary>
    /// 實際呼叫一次文字生成與語音轉錄（經 AiCallGateway，記入用量與 AI 對話紀錄）。
    /// ⚠️ 會產生費用：一定先確認（開發慣例 §6.3）。與「重新檢查」互斥——兩者都會用到同一個 circuit 的 DbContext。
    /// </summary>
    private async Task OnTestAiConnectionAsync()
    {
        if (isLoading || isTestingAi || !AuthenticationStateHelper.CheckIsAdmin())
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認測試 AI 連線（會產生費用）",
            Content = "將實際呼叫 Azure OpenAI 兩次：文字生成送一則極短提問、語音轉錄送 1 秒靜音，用來確認部署存在、金鑰可用。"
                + "費用極少，會記入 Token 用量（功能別「AI 連線測試」）與 AI 對話紀錄，也受每月用量上限管制。確定要繼續嗎？",
            OkText = "開始測試",
            CancelText = "取消",
            MaskClosable = false,
        });

        if (!confirmed)
        {
            return;
        }

        isTestingAi = true;
        StateHasChanged();

        try
        {
            aiProbeResults = await AiConnectionTestService.RunAsync();
            aiProbeTestedAt = DateTime.Now;

            var summary = string.Join("; ", aiProbeResults.Select(x => $"{x.Item}={(x.Success ? "OK" : "Fail")}"));
            await AuditLogService.WriteForCurrentUserAsync(
                AuditActions.AiConnectionTest,
                success: aiProbeResults.All(x => x.Success),
                targetType: "LlmSettings",
                targetId: "*",
                detail: summary);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI connection test failed unexpectedly.");
            aiProbeResults = [new AiConnectionProbeResult("測試", false, string.Empty, string.Empty, null, $"測試失敗：{ex.Message}")];
            aiProbeTestedAt = DateTime.Now;
        }
        finally
        {
            isTestingAi = false;
            StateHasChanged();
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
