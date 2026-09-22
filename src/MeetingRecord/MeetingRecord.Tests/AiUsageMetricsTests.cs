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

    #region 本月累計（0.4.91）

    private static (DateOnly, decimal) At(int year, int month, int day, decimal amount)
        => (new DateOnly(year, month, day), amount);

    [Fact]
    public void Cumulative_ShouldHaveOnePointPerDayUpToToday()
    {
        // X 軸是本月 1 日到今天——不畫到月底，今天之後沒有資料。
        var today = new DateOnly(2026, 9, 22);

        var points = AiUsageMetrics.BuildMonthToDateCumulative([], [], today);

        Assert.Equal(22, points.Count);
    }

    [Fact]
    public void Cumulative_LastPoint_ShouldEqualTheMonthTotal()
    {
        // ⭐ 最後一點就是卡片上「本月估算金額」——曲線是那張卡的趨勢版，兩邊必須對得上。
        var today = new DateOnly(2026, 9, 22);
        var thisMonth = new[] { At(2026, 9, 3, 1.5m), At(2026, 9, 10, 2m), At(2026, 9, 22, 0.25m) };
        var lastMonth = new[] { At(2026, 8, 5, 4m), At(2026, 8, 20, 1m) };

        var points = AiUsageMetrics.BuildMonthToDateCumulative(thisMonth, lastMonth, today);

        Assert.Equal(375, points[^1].Created);    // 3.75 → 375 分
        Assert.Equal(500, points[^1].Completed);  // 5.00 → 500 分
    }

    [Fact]
    public void Cumulative_ShouldNeverGoDown()
    {
        // 累計不可能往下掉。會往下掉的話，就是有哪一天被重設成 0 了。
        var today = new DateOnly(2026, 9, 22);
        var thisMonth = new[] { At(2026, 9, 1, 1m), At(2026, 9, 15, 2m) };
        var lastMonth = new[] { At(2026, 8, 2, 3m), At(2026, 8, 21, 1m) };

        var points = AiUsageMetrics.BuildMonthToDateCumulative(thisMonth, lastMonth, today);

        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].Created >= points[i - 1].Created, $"本月第 {i + 1} 天往下掉了。");
            Assert.True(points[i].Completed >= points[i - 1].Completed, $"上月第 {i + 1} 天往下掉了。");
        }
    }

    [Fact]
    public void Cumulative_ShorterPreviousMonth_ShouldCarryTheMonthEndValue()
    {
        // ⭐ 3/31 對上 2 月：2 月只有 28 天，第 29～31 天的上月值要維持 2/28 的累計，不可以掉回 0。
        // 這與 BuildMonthToDateRanges 把上月迄日夾到最後一天的規則一致——兩邊對不上的話，
        // 月底那幾天曲線的最後一點會與卡片數字不符。
        var today = new DateOnly(2026, 3, 31);
        var lastMonth = new[] { At(2026, 2, 10, 1m), At(2026, 2, 28, 2m) };

        var points = AiUsageMetrics.BuildMonthToDateCumulative([], lastMonth, today);

        Assert.Equal(31, points.Count);
        Assert.Equal(300, points[27].Completed);  // 第 28 天
        Assert.Equal(300, points[28].Completed);  // 第 29 天：上月沒有這一天
        Assert.Equal(300, points[30].Completed);  // 第 31 天
    }

    [Fact]
    public void Cumulative_ShouldAccumulateBeforeRoundingToCents()
    {
        // ⭐ 一次 AI 問答大約 NT$0.0014，換成「分」是 0.14、四捨五入成 0。
        // 每筆先換成分再加的話，一千次問答加起來仍然是 0——曲線會是一條平的線。
        var today = new DateOnly(2026, 9, 22);
        var tiny = Enumerable.Range(0, 1000).Select(_ => At(2026, 9, 5, 0.0014m));

        var points = AiUsageMetrics.BuildMonthToDateCumulative(tiny, [], today);

        Assert.Equal(140, points[^1].Created);  // 1.4 → 140 分
    }

    [Fact]
    public void Cumulative_ShouldIgnoreRowsOutsideTheRange()
    {
        // 呼叫端傳錯範圍時，不可以畫出超出今天的點或把別的月份算進來。
        var today = new DateOnly(2026, 9, 22);
        var thisMonth = new[] { At(2026, 9, 25, 9m), At(2026, 7, 1, 9m), At(2026, 9, 1, 1m) };

        var points = AiUsageMetrics.BuildMonthToDateCumulative(thisMonth, [], today);

        Assert.Equal(100, points[^1].Created);
    }

    [Fact]
    public void Cumulative_Empty_ShouldBeAllZeros()
    {
        var points = AiUsageMetrics.BuildMonthToDateCumulative([], [], new DateOnly(2026, 9, 1));

        var point = Assert.Single(points);
        Assert.Equal(0, point.Created);
        Assert.Equal(0, point.Completed);
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
