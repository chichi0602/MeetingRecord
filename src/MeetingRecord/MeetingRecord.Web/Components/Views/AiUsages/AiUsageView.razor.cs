using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.AiUsages;

/// <summary>
/// AI 用量分析。**管理員限定**——這一頁看得到全公司的用量與人名，而且不套團隊過濾。
///
/// <para>
/// 資料來自 0.4.80 起累積的用量帳本，**沒有回填**：畫面上一定要寫出統計起始日，
/// 否則使用者會以為系統在此之前都沒有花錢。
/// </para>
/// </summary>
public partial class AiUsageView : ComponentBase
{
    /// <summary>「全部功能」在下拉裡的值。用空字串而不是 null，AntDesign 的 Select 才綁得住。</summary>
    private const string AllFeatures = "";

    /// <summary>趨勢圖可選的天數，比照儀表板。</summary>
    private static readonly int[] TrendDayOptions = [7, 30, 90];

    private static readonly AiUsageFeature[] AllFeatureValues = Enum.GetValues<AiUsageFeature>();

    [Inject]
    private AiUsageAnalysisService AiUsageAnalysisService { get; set; } = default!;

    [Inject]
    private ILogger<AiUsageView> Logger { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private string RoleMessage { get; set; } = string.Empty;

    private ITable? table;
    private AiUsageSummary? summary;
    private IReadOnlyList<AiUsageRow> rows = [];

    /// <summary>
    /// 明細表的期間（最近 N 天）。0.4.91 起**只影響明細表**：曲線改成本月累計之後，
    /// 摘要（卡片、曲線、分佈）一律是本月至今，不再依賴這個值。
    /// </summary>
    private int trendDays = 30;
    private string featureFilter = AllFeatures;

    private int pageIndex = 1;
    private int pageSize = 20;
    private int total;

    private bool isLoading;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogInformation("Initializing AI usage view.");

        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("AI usage initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_AI用量分析) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("AI usage denied because current user has not this role permission.");
            return;
        }

        await ReloadAsync();
    }

    /// <summary>
    /// 重新載入摘要與明細第一頁。
    /// 開頭的早退不可省：載入中切換期間會讓下拉與畫面永久不同步（同儀表板的既有註解）。
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
            summary = await AiUsageAnalysisService.GetSummaryAsync(ParseFeature(featureFilter));
            pageIndex = 1;
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI usage summary failed.");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task LoadRowsAsync()
    {
        var now = DateTime.Now;

        var result = await AiUsageAnalysisService.GetRecentCallsAsync(new AiUsageQuery(
            From: now.Date.AddDays(-(trendDays - 1)),
            // 用「明天零時」當上界，today 當天的紀錄才不會被切掉。
            ToExclusive: now.Date.AddDays(1),
            Feature: ParseFeature(featureFilter),
            CurrentPage: pageIndex,
            PageSize: pageSize));

        total = result.TotalCount;
        rows = result.Rows;
    }

    private static AiUsageFeature? ParseFeature(string value)
        => Enum.TryParse<AiUsageFeature>(value, out var feature) ? feature : null;

    /// <summary>是否為「全部功能」。選了特定功能時功能別圓餅只剩一片 100%，畫面上就不渲染。</summary>
    private bool IsAllFeatures => ParseFeature(featureFilter) is null;

    /// <summary>
    /// 換期間。**只重載明細表**，不重算整頁摘要——期間已經不影響摘要了（見 <see cref="trendDays"/>）。
    /// 回到第一頁：換了期間之後，原本的頁碼很可能已經超出範圍。
    /// </summary>
    private async Task OnTrendDaysChangedAsync(int value)
    {
        if (isLoading)
        {
            return;
        }

        trendDays = value;
        isLoading = true;
        try
        {
            pageIndex = 1;
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI usage rows failed.");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnFeatureChangedAsync(string value)
    {
        featureFilter = value ?? AllFeatures;
        await ReloadAsync();
    }

    /// <summary>翻頁。只重載明細，不重算整頁摘要——摘要與分頁無關。</summary>
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
            await LoadRowsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI usage rows failed.");
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }
}
