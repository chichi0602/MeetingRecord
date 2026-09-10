using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 儀表板純計算的單元測試。重點在幾個「錯了畫面不會壞、只會默默說謊」的地方：
/// 沒有資料的月份、除以零、以及圓餅只有單一切片時的整圓分支。
/// </summary>
public sealed class DashboardMetricsTests
{
    #region 月份分桶

    [Fact]
    public void BuildMonthBuckets_ShouldReturnRequestedCountOldestFirst()
    {
        var buckets = DashboardMetrics.BuildMonthBuckets(new DateOnly(2026, 9, 8), 12);

        Assert.Equal(12, buckets.Count);

        // 由舊到新，最後一個是當月。
        Assert.Equal(new DateOnly(2025, 10, 1), buckets[0]);
        Assert.Equal(new DateOnly(2026, 9, 1), buckets[^1]);
    }

    [Fact]
    public void BuildMonthBuckets_ShouldIncludeEveryMonthEvenAcrossYearBoundary()
    {
        var buckets = DashboardMetrics.BuildMonthBuckets(new DateOnly(2026, 2, 15), 4);

        // 沒有資料的月份也必須在——只畫有資料的月份會讓時間軸變成不等距，
        // 相隔半年的兩個點會被讀成連續兩個月。
        Assert.Equal(
            [
                new DateOnly(2025, 11, 1),
                new DateOnly(2025, 12, 1),
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 2, 1),
            ],
            buckets);
    }

    [Fact]
    public void BuildMonthBuckets_ShouldNormaliseToFirstOfMonth()
    {
        var buckets = DashboardMetrics.BuildMonthBuckets(new DateOnly(2026, 9, 30), 1);

        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(buckets));
    }

    [Fact]
    public void BuildMonthBuckets_ShouldRejectNonPositiveCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DashboardMetrics.BuildMonthBuckets(new DateOnly(2026, 9, 8), 0));
    }

    #endregion

    #region 百分比

    [Fact]
    public void ToPercentages_ShouldSumToOneHundred()
    {
        var percentages = DashboardMetrics.ToPercentages([1, 1, 2]);

        Assert.Equal(25d, percentages[0], 3);
        Assert.Equal(25d, percentages[1], 3);
        Assert.Equal(50d, percentages[2], 3);
        Assert.Equal(100d, percentages.Sum(), 3);
    }

    [Fact]
    public void ToPercentages_AllZero_ShouldNotDivideByZero()
    {
        var percentages = DashboardMetrics.ToPercentages([0, 0, 0]);

        Assert.All(percentages, value => Assert.Equal(0d, value));
    }

    #endregion

    #region 圓餅幾何

    [Fact]
    public void BuildArcPath_QuarterSlice_ShouldStartAtTopAndSweepClockwise()
    {
        // 0 度在正上方（一般人讀圓餅的起點），順時針掃 90 度後應停在正右方。
        // 半徑 40、圓心 (50,50)：起點 (50,10) 是正上方，終點 (90,50) 是正右方。
        var path = DashboardMetrics.BuildArcPath(50, 50, 40, 0, 90);

        Assert.Equal("M 50 50 L 50 10 A 40 40 0 0 1 90 50 Z", path);
    }

    [Fact]
    public void BuildArcPath_FullCircle_ShouldUseTwoArcsInsteadOfDegenerateSweep()
    {
        // 只有一個切片時起訖角重合，標準 arc 會畫不出東西（等於「從 A 畫弧到 A」），
        // 整個圓餅會變空白。必須改用兩段半圓。
        var path = DashboardMetrics.BuildArcPath(50, 50, 40, 0, 360);

        Assert.Equal(2, path.Split(" A ").Length - 1);
        Assert.DoesNotContain("Z", path, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArcPath_LargeSlice_ShouldSetLargeArcFlag()
    {
        // 超過半圓要把 large-arc-flag 設為 1，否則 SVG 會畫成互補的那一小塊。
        var path = DashboardMetrics.BuildArcPath(50, 50, 40, 0, 270);

        Assert.Contains("0 1 1", path, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArcPath_ShouldRejectNonPositiveRadius()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DashboardMetrics.BuildArcPath(50, 50, 0, 0, 90));
    }

    #endregion

    #region 耗時與失敗率

    [Fact]
    public void DescribeDuration_Null_ShouldNotReadAsZero()
    {
        // 「0 秒」會被讀成「非常快」，但實際上是還沒有任何一筆完成。
        Assert.Equal("—", DashboardMetrics.DescribeDuration(null));
    }

    [Theory]
    [InlineData(45, "45 秒")]
    [InlineData(83, "1 分 23 秒")]
    public void DescribeDuration_ShouldPickReadableUnit(int seconds, string expected)
    {
        Assert.Equal(expected, DashboardMetrics.DescribeDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void DescribeDuration_OverAnHour_ShouldUseHoursAndMinutes()
    {
        Assert.Equal("2 小時 5 分", DashboardMetrics.DescribeDuration(new TimeSpan(2, 5, 30)));
    }

    [Fact]
    public void CalculateFailureRate_NoDataYet_ShouldReturnNullNotZero()
    {
        // 回 0 會被讀成「成功率 100%」，但實際上只是還沒有資料。
        Assert.Null(DashboardMetrics.CalculateFailureRate(0, 0));
    }

    [Fact]
    public void CalculateFailureRate_ShouldUseCompletedPlusFailedAsDenominator()
    {
        Assert.Equal(25d, DashboardMetrics.CalculateFailureRate(3, 1));
    }

    [Fact]
    public void AverageDuration_ShouldIgnoreRangesWithoutProgress()
    {
        var start = new DateTime(2026, 9, 8, 10, 0, 0);

        var average = DashboardMetrics.AverageDuration(
        [
            (start, start.AddSeconds(10)),
            (start, start.AddSeconds(30)),
            (start, start),                 // 沒有實際耗時，不該被算進平均
        ]);

        Assert.Equal(TimeSpan.FromSeconds(20), average);
    }

    [Fact]
    public void AverageDuration_NoCompleteRange_ShouldReturnNull()
    {
        Assert.Null(DashboardMetrics.AverageDuration([]));
    }

    #endregion

    #region 檔案大小

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FileSizeFormatter_ShouldStepThroughUnits(long bytes, string expected)
    {
        Assert.Equal(expected, FileSizeFormatter.Describe(bytes));
    }

    [Fact]
    public void FileSizeFormatter_Negative_ShouldClampToZero()
    {
        Assert.Equal("0 B", FileSizeFormatter.Describe(-1));
    }

    #endregion
}
