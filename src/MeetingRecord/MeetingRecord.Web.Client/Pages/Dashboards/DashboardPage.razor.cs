using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Dashboards;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Web.Client.Pages.Dashboards;

/// <summary>
/// 儀表板（1.1.12 從 Blazor Server 的 DashboardView 搬到 WASM）。所有數字都取自既有欄位，本頁不寫入任何資料；
/// 資料改走 <see cref="DashboardApi"/>，看不到的專案名稱由伺服器改成「其他專案」。
/// </summary>
public partial class DashboardPage : ComponentBase
{
    /// <summary>超過這個比例就把失敗率標紅——偶爾一兩次失敗是常態，持續高失敗才需要注意。</summary>
    private const double FailureRateWarningThreshold = 20d;

    [Inject]
    private DashboardApi DashboardApi { get; set; } = default!;

    [Inject]
    private ILogger<DashboardPage> Logger { get; set; } = default!;

    private DashboardSummary? summary;
    private bool isLoading;

    private bool IsFailureRateHigh =>
        summary?.Performance.TranscriptionFailureRate is { } rate && rate > FailureRateWarningThreshold;

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態由 WasmLayout 先處理過。0.4.97 起登入即可看，不檢查頁面權限：儀表板只放全公司的彙總數字，不含任何明細。
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
            var result = await DashboardApi.GetAsync();
            if (result.Success)
            {
                summary = result.Data;
            }
            else
            {
                Logger.LogWarning("Loading dashboard summary failed. Status={Status}, Message={Message}", result.StatusCode, result.ErrorMessage ?? result.Message);
            }
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
        => FormatPercent(summary?.Performance.TranscriptionFailureRate);

    /// <summary>百分比；沒有資料（null）時顯示「—」，理由同失敗率。</summary>
    private static string FormatPercent(double? value)
        => value is { } rate
            ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";
}
