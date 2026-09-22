using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Tests;

/// <summary>
/// 用量數字的純函式（0.4.80）。這裡守的多半是「一年只錯幾天，而錯的那幾天沒人在看」的邊界。
/// </summary>
public sealed class AiUsageMetricsTests
{
    #region 本月至今 vs 上月同期

    [Fact]
    public void BuildMonthToDateRanges_ShouldCompareAgainstTheSamePeriod()
    {
        // ⚠️ 比較的必須是上月**同期**。拿本月 3 天去比上月整整 31 天，
        // 卡片上會顯示「-89%」，看起來像系統壞了。
        var (currentFrom, previousFrom, previousToExclusive) =
            AiUsageMetrics.BuildMonthToDateRanges(new DateTime(2026, 5, 3, 14, 30, 0));

        Assert.Equal(new DateTime(2026, 5, 1), currentFrom);
        Assert.Equal(new DateTime(2026, 4, 1), previousFrom);

        // 上月同期只到 4/3，不是整個 4 月。
        Assert.Equal(3, previousToExclusive.Day);
        Assert.Equal(4, previousToExclusive.Month);
    }

    [Fact]
    public void BuildMonthToDateRanges_ShouldClampWhenPreviousMonthIsShorter()
    {
        // ⚠️ 3/31 的上個月是 2 月，沒有 31 號。硬算會拋例外或滑到 3 月，
        // 兩者都會讓這一天的卡片顯示錯誤的數字。
        var (_, _, previousToExclusive) =
            AiUsageMetrics.BuildMonthToDateRanges(new DateTime(2026, 3, 31, 9, 0, 0));

        Assert.Equal(2, previousToExclusive.Month);
        Assert.Equal(28, previousToExclusive.Day);
    }

    [Fact]
    public void BuildMonthToDateRanges_ShouldHandleLeapYear()
    {
        var (_, _, previousToExclusive) =
            AiUsageMetrics.BuildMonthToDateRanges(new DateTime(2024, 3, 31, 9, 0, 0));

        Assert.Equal(29, previousToExclusive.Day);
    }

    [Fact]
    public void BuildMonthToDateRanges_ShouldCrossTheYearBoundary()
    {
        var (currentFrom, previousFrom, _) =
            AiUsageMetrics.BuildMonthToDateRanges(new DateTime(2026, 1, 10));

        Assert.Equal(new DateTime(2026, 1, 1), currentFrom);
        Assert.Equal(new DateTime(2025, 12, 1), previousFrom);
    }

    #endregion

    #region 變化率

    [Fact]
    public void CalculateChangeRate_ShouldReturnNull_WhenPreviousIsZero()
    {
        // ⚠️ 從零成長的百分比沒有意義。硬算會是無限大或除以零；
        // 回 0% 則會謊稱「跟上個月一樣」。畫面要顯示「上月同期無資料」。
        Assert.Null(AiUsageMetrics.CalculateChangeRate(12.34m, 0m));
    }

    [Fact]
    public void CalculateChangeRate_ShouldComputeBothDirections()
    {
        Assert.Equal(50d, AiUsageMetrics.CalculateChangeRate(15m, 10m)!.Value, precision: 6);
        Assert.Equal(-50d, AiUsageMetrics.CalculateChangeRate(5m, 10m)!.Value, precision: 6);
        Assert.Equal(0d, AiUsageMetrics.CalculateChangeRate(10m, 10m)!.Value, precision: 6);
    }

    #endregion

    #region 每日數列

    [Fact]
    public void BuildDailySeries_ShouldIncludeDaysWithoutData()
    {
        // ⚠️ 沒有任何呼叫的那一天也必須在圖上佔一格。迭代「有資料的日子」的話，
        // 折線會把相隔一週的兩個點畫成相鄰，看起來像天天都在花錢。
        var today = new DateOnly(2026, 9, 17);
        var items = new[]
        {
            (Day: new DateOnly(2026, 9, 11), SeriesOne: 100, SeriesTwo: 0),
            (Day: new DateOnly(2026, 9, 17), SeriesOne: 0, SeriesTwo: 200),
        };

        var points = AiUsageMetrics.BuildDailySeries(items, today, days: 7);

        Assert.Equal(7, points.Count);
        Assert.Equal(100, points[0].Created);
        Assert.Equal(200, points[6].Completed);
        Assert.All(points.Skip(1).Take(5), p => Assert.Equal(0, p.Created + p.Completed));
    }

    [Fact]
    public void BuildDailySeries_ShouldSumMultipleCallsOnTheSameDay()
    {
        var today = new DateOnly(2026, 9, 17);
        var items = new[]
        {
            (Day: today, SeriesOne: 10, SeriesTwo: 1),
            (Day: today, SeriesOne: 20, SeriesTwo: 2),
        };

        var points = AiUsageMetrics.BuildDailySeries(items, today, days: 7);

        Assert.Equal(30, points[^1].Created);
        Assert.Equal(3, points[^1].Completed);
    }

    #endregion

    #region 顯示格式

    [Fact]
    public void FormatAmount_ShouldShowDashWhenUnknown()
    {
        // ⚠️ 單價未設定時必須是「—」。顯示 $0.00 與「真的沒花錢」看起來一模一樣。
        Assert.Equal("—", AiUsageMetrics.FormatAmount(null, "USD"));
    }

    [Fact]
    public void FormatAmount_ShouldKeepSmallAmountsVisible()
    {
        // 一次短問答可能只有 0.000045。兩位小數會把它變成 0.00，看起來像免費。
        var text = AiUsageMetrics.FormatAmount(0.000045m, "USD");

        Assert.Contains("0.000045", text);
    }

    [Fact]
    public void FormatAmount_ShouldUseTwoDecimalsForLargerAmounts()
    {
        Assert.Equal("USD 12.34", AiUsageMetrics.FormatAmount(12.34m, "USD"));
    }

    [Fact]
    public void ToChartCents_ShouldClampNegativeAndOverflow()
    {
        // 幾何用的整數不可以是負的——負長條在圖上只會看起來很怪。
        Assert.Equal(0, AiUsageMetrics.ToChartCents(-5m));
        Assert.Equal(1234, AiUsageMetrics.ToChartCents(12.34m));
        Assert.Equal(int.MaxValue, AiUsageMetrics.ToChartCents(decimal.MaxValue / 2));
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5K")]
    [InlineData(2_400_000, "2.4M")]
    public void FormatTokens_ShouldAbbreviate(long tokens, string expected)
        => Assert.Equal(expected, AiUsageMetrics.FormatTokens(tokens));

    [Fact]
    public void FormatAudio_ShouldSwitchToHours()
    {
        Assert.Equal("0 分鐘", AiUsageMetrics.FormatAudio(0));
        Assert.Equal("15 分鐘", AiUsageMetrics.FormatAudio(900));
        Assert.Equal("2.5 小時", AiUsageMetrics.FormatAudio(9000));
    }

    [Fact]
    public void FormatConverted_Null_ShouldBeDash()
    {
        // 沒單價或沒匯率時是「—」而不是「NT$ 0」——後者與「真的沒花錢」看起來一模一樣。
        Assert.Equal("—", AiUsageMetrics.FormatConverted(null, "TWD"));
    }

    [Fact]
    public void FormatConverted_TinyAmount_ShouldNotCollapseToZero()
    {
        // ⭐ 一次 AI 問答大約是 NT$0.0014。用兩位小數會讓整張明細表看起來全部免費。
        var text = AiUsageMetrics.FormatConverted(0.0014m, "TWD");

        Assert.NotEqual("NT$ 0.00", text);
        Assert.Contains("0.0014", text);
    }

    [Fact]
    public void FormatConverted_LargeAmount_ShouldUseThousandsSeparator()
    {
        Assert.Equal("NT$ 1,234.56", AiUsageMetrics.FormatConverted(1234.56m, "TWD"));
    }

    [Fact]
    public void FormatConverted_OtherCurrency_ShouldPrintTheCode()
    {
        // 台幣用習慣的符號，其他幣別就印代碼——不去猜每一種幣別的符號。
        Assert.Equal("JPY 1,234.56", AiUsageMetrics.FormatConverted(1234.56m, "JPY"));
    }

    #endregion
}
