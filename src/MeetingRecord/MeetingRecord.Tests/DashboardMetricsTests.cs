using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 儀表板純計算的單元測試。重點在幾個「錯了畫面不會壞、只會默默說謊」的地方：
/// 沒有資料的日子、窗口邊界、除以零、以及圓餅只有單一切片時的整圓分支。
/// </summary>
public sealed class DashboardMetricsTests
{
    #region 日期分桶

    [Fact]
    public void BuildDayBuckets_ShouldReturnRequestedCountOldestFirst()
    {
        var buckets = DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 7);

        Assert.Equal(7, buckets.Count);

        // 桶含今天，所以起點是 today − 6 而不是 today − 7。
        // 最右邊必須正好是今天——那是讀者定位用的錨點。
        Assert.Equal(new DateOnly(2026, 9, 2), buckets[0]);
        Assert.Equal(new DateOnly(2026, 9, 8), buckets[^1]);
    }

    [Fact]
    public void BuildDayBuckets_ShouldIncludeEveryDayAcrossMonthBoundary()
    {
        var buckets = DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 3, 2), 4);

        // 沒有資料的日子也必須在——只畫有資料的日子會讓時間軸變成不等距，
        // 相隔兩週的兩個點會被讀成連續兩天。2026 不是閏年，所以 2 月只有 28 天。
        Assert.Equal(
            [
                new DateOnly(2026, 2, 27),
                new DateOnly(2026, 2, 28),
                new DateOnly(2026, 3, 1),
                new DateOnly(2026, 3, 2),
            ],
            buckets);
    }

    [Fact]
    public void BuildDayBuckets_ShouldSpanYearBoundary()
    {
        var buckets = DashboardMetrics.BuildDayBuckets(new DateOnly(2027, 1, 15), 90);

        // 最長的範圍會跨年，桶數不能因此少算——月份長度不一是最容易出錯的地方。
        Assert.Equal(90, buckets.Count);
        Assert.Equal(new DateOnly(2026, 10, 18), buckets[0]);
        Assert.Equal(new DateOnly(2027, 1, 15), buckets[^1]);
    }

    [Fact]
    public void BuildDayBuckets_ShouldRejectNonPositiveCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 0));
    }

    #endregion

    #region 日期標籤

    [Fact]
    public void BuildDayLabels_ShouldKeepLengthEqualToDayCount()
    {
        var labels = DashboardMetrics.BuildDayLabels(
            DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 90));

        // 這是整個稀疏化方案的核心不變量：空字串仍要佔一個位置。
        // 畫面把標籤排成等寬的 flex 槽位，少一個槽位，其後所有可見標籤都會與資料點錯位。
        Assert.Equal(90, labels.Count);
    }

    [Fact]
    public void BuildDayLabels_ShouldAlwaysLabelTheMostRecentDay()
    {
        var labels = DashboardMetrics.BuildDayLabels(
            DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 90));

        // 最右邊的刻度是讀者定位的錨點，稀疏化不能把它算掉。
        Assert.Equal("09/08", labels[^1]);
    }

    [Fact]
    public void BuildDayLabels_ShouldLabelEveryDayWhenRangeIsShort()
    {
        var labels = DashboardMetrics.BuildDayLabels(
            DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 7));

        // 7 天塞得下 7 個標籤，沒有稀疏化的理由。
        Assert.DoesNotContain(string.Empty, labels);
    }

    [Fact]
    public void BuildDayLabels_ShouldKeepVisibleLabelsWithinBudget()
    {
        var labels = DashboardMetrics.BuildDayLabels(
            DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 90));

        // 90 個 MM/dd 全畫會疊成一團。
        Assert.True(labels.Count(label => label.Length > 0) <= 7);
    }

    [Fact]
    public void BuildDayLabels_ShouldNotThrowWhenCountIsBelowLabelBudget()
    {
        // 天數少於標籤上限時，step 若用整數除法會算出 0，接下來的 % step 就是除以零。
        var labels = DashboardMetrics.BuildDayLabels(
            DashboardMetrics.BuildDayBuckets(new DateOnly(2026, 9, 8), 5));

        Assert.Equal(5, labels.Count);
        Assert.DoesNotContain(string.Empty, labels);
    }

    #endregion

    #region 日趨勢

    [Fact]
    public void BuildDailyTrend_ShouldPlaceEventsIntoTheirOwnDay()
    {
        // 一天的頭尾兩個極端時間：時間部分不能讓事件落到前一天或隔天。
        // 這裡若誤加 ToLocalTime()（欄位是 DateTime.Now 寫入、Kind 為 Unspecified），
        // 整批會位移 8 小時，23:59 那筆就會跑到 9/7。
        (DateTime, DateTime?)[] meetings =
        [
            (new DateTime(2026, 9, 6, 23, 59, 0), new DateTime(2026, 9, 8, 0, 1, 0)),
        ];

        var points = DashboardMetrics.BuildDailyTrend(meetings, new DateOnly(2026, 9, 8), 7);

        Assert.Equal(7, points.Count);
        Assert.Equal(1, points[4].Created);     // 9/6
        Assert.Equal(1, points[^1].Completed);  // 9/8
        Assert.Equal(1, points.Sum(point => point.Created));
        Assert.Equal(1, points.Sum(point => point.Completed));
    }

    [Fact]
    public void BuildDailyTrend_ShouldIgnoreMeetingsOutsideTheWindow()
    {
        (DateTime, DateTime?)[] meetings = [(new DateTime(2026, 8, 1), null)];

        var points = DashboardMetrics.BuildDailyTrend(meetings, new DateOnly(2026, 9, 8), 7);

        // 窗外的舊資料不能被堆到第一個桶——迭代分組結果而不是迭代日期桶就會犯這個錯。
        Assert.Equal(0, points.Sum(point => point.Created));
    }

    [Fact]
    public void BuildDailyTrend_ShouldIgnoreNullDraftCompletedAt()
    {
        (DateTime, DateTime?)[] meetings = [(new DateTime(2026, 9, 8, 10, 0, 0), null)];

        var points = DashboardMetrics.BuildDailyTrend(meetings, new DateOnly(2026, 9, 8), 7);

        // 「新增了會議」與「完成了紀錄」是兩條獨立的線，還沒生成不能算成已完成。
        Assert.Equal(1, points[^1].Created);
        Assert.Equal(0, points[^1].Completed);
    }

    [Fact]
    public void BuildDailyTrend_ShouldReturnZeroFilledPointsWhenNoMeetings()
    {
        var points = DashboardMetrics.BuildDailyTrend([], new DateOnly(2026, 9, 8), 30);

        // 完全沒有資料時要回 30 個零點而不是空集合：畫面的空狀態文案是靠
        // 「最大值為 0」判斷的，回空集合會變成另一個分支（「尚無資料」）。
        Assert.Equal(30, points.Count);
        Assert.All(points, point =>
        {
            Assert.Equal(0, point.Created);
            Assert.Equal(0, point.Completed);
            Assert.NotNull(point.Label);
        });
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
