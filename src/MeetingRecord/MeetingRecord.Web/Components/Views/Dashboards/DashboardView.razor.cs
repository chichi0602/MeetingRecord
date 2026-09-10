using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Dashboards;

/// <summary>
/// 儀表板。所有數字都取自既有欄位，本頁不寫入任何資料。
/// </summary>
public partial class DashboardView : ComponentBase
{
    /// <summary>超過這個比例就把失敗率標紅——偶爾一兩次失敗是常態，持續高失敗才需要注意。</summary>
    private const double FailureRateWarningThreshold = 20d;

    [Inject]
    private DashboardService DashboardService { get; set; } = default!;

    [Inject]
    private ILogger<DashboardView> Logger { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    private DashboardSummary? summary;
    private bool isLoading;
    private string RoleMessage = string.Empty;

    private bool IsFailureRateHigh =>
        summary?.Performance.TranscriptionFailureRate is { } rate && rate > FailureRateWarningThreshold;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogInformation("Initializing dashboard view.");

        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Dashboard initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_儀表板) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Dashboard denied because current user has not this role permission.");
            return;
        }

        await ReloadAsync();
    }

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
            summary = await DashboardService.GetSummaryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading dashboard summary failed.");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 還沒有任何成功或失敗的轉錄時顯示「—」而不是 0%——
    /// 0% 會被讀成「成功率 100%」，但實際上只是還沒有資料。
    /// </summary>
    private string FormatFailureRate()
        => summary?.Performance.TranscriptionFailureRate is { } rate
            ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";
}
