using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 日誌檢視快速區間（最近 5 分／15 分／1 小時／今天）與「查前後 1 分鐘」的起訖計算。
/// </summary>
public sealed class LogTimeRangesTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 8, 9, 9, 500);

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    public void Last_ShouldEndAtNowAndStartSpanEarlier(int minutes)
    {
        var (start, end) = LogTimeRanges.Last(TimeSpan.FromMinutes(minutes), Now);

        Assert.Equal(Now.AddMinutes(-minutes), start);
        Assert.Equal(Now, end);
    }

    [Fact]
    public void Today_ShouldStartAtMidnightAndEndAtNow()
    {
        var (start, end) = LogTimeRanges.Today(Now);

        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0), start);
        Assert.Equal(Now, end);
    }

    [Fact]
    public void Around_ShouldSpanOneMinuteEachSide()
    {
        var timestamp = new DateTime(2026, 10, 5, 5, 30, 57, 246);

        var (start, end) = LogTimeRanges.Around(timestamp);

        Assert.Equal(new DateTime(2026, 10, 5, 5, 29, 57, 246), start);
        Assert.Equal(new DateTime(2026, 10, 5, 5, 31, 57, 246), end);
    }
}
