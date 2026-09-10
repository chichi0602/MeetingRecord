using System.Globalization;

namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>
/// 儀表板用到的純計算：月份分桶、圓餅幾何、百分比與耗時描述。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker</c>、<c>ChatContextBuilder</c>、
/// <c>TranscriptionNoiseFilter</c> 同一個慣例，不碰 IO、不碰資料庫。
/// </summary>
public static class DashboardMetrics
{
    /// <summary>
    /// 產生「最近 N 個月」的月份桶，由舊到新。
    ///
    /// <para>
    /// **沒有資料的月份也一定會在**——趨勢圖若只畫有資料的月份，
    /// 時間軸會被壓縮成不等距，兩個相隔半年的點看起來會像連續兩個月。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DateOnly> BuildMonthBuckets(DateOnly today, int months)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(months);

        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);

        return [.. Enumerable
            .Range(0, months)
            .Select(offset => firstOfThisMonth.AddMonths(offset - (months - 1)))];
    }

    /// <summary>月份桶的顯示標籤。跨年時帶出年份，否則只顯示月份，避免軸線過擠。</summary>
    public static string DescribeMonth(DateOnly month, bool includeYear)
        => includeYear
            ? month.ToString("yyyy/MM", CultureInfo.InvariantCulture)
            : month.ToString("MM", CultureInfo.InvariantCulture) + "月";

    /// <summary>
    /// 換算各項佔比（0～100）。總和為 0 時全部回 0——**不能除以零**，
    /// 而且「沒有資料」與「每項都佔 0%」在畫面上要能區分（由呼叫端判斷是否顯示空狀態）。
    /// </summary>
    public static IReadOnlyList<double> ToPercentages(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var total = values.Sum();
        if (total <= 0)
        {
            return [.. values.Select(_ => 0d)];
        }

        return [.. values.Select(value => value * 100d / total)];
    }

    /// <summary>
    /// 產生圓餅（甜甜圈）單一切片的 SVG path。
    ///
    /// <para>
    /// <paramref name="sweepDegrees"/> 達到 360 度時**必須特判**：SVG 的 arc 在起點與終點
    /// 重合時畫不出東西（等於要求「從 A 畫弧到 A」，繪圖引擎會直接略過），
    /// 整個圓餅只有一個切片時就會變成空白。這裡改用兩段半圓接成整圓。
    /// </para>
    /// </summary>
    public static string BuildArcPath(
        double centerX,
        double centerY,
        double radius,
        double startDegrees,
        double sweepDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        // 用 >= 359.999 而不是 == 360：角度是累加出來的浮點數，不會剛好等於 360。
        if (sweepDegrees >= 359.999)
        {
            var top = FormatPoint(centerX, centerY - radius);
            var bottom = FormatPoint(centerX, centerY + radius);
            var r = Format(radius);

            return $"M {top} A {r} {r} 0 1 1 {bottom} A {r} {r} 0 1 1 {top}";
        }

        var start = PointOnCircle(centerX, centerY, radius, startDegrees);
        var end = PointOnCircle(centerX, centerY, radius, startDegrees + sweepDegrees);
        var largeArc = sweepDegrees > 180 ? 1 : 0;

        return $"M {FormatPoint(centerX, centerY)} L {start} A {Format(radius)} {Format(radius)} 0 {largeArc} 1 {end} Z";
    }

    /// <summary>耗時的顯示文字。沒有資料時回「—」，不要回「0 秒」——那會被誤讀成「非常快」。</summary>
    public static string DescribeDuration(TimeSpan? duration)
    {
        if (duration is null || duration.Value < TimeSpan.Zero)
        {
            return "—";
        }

        var value = duration.Value;

        if (value.TotalMinutes < 1)
        {
            return $"{value.TotalSeconds:0} 秒";
        }

        if (value.TotalHours < 1)
        {
            return $"{value.Minutes} 分 {value.Seconds} 秒";
        }

        return $"{(int)value.TotalHours} 小時 {value.Minutes} 分";
    }

    /// <summary>
    /// 失敗率（0～100）。分母為 0（還沒有任何一筆跑完或失敗）時回 null，
    /// 由畫面顯示「—」——回 0 會讓人以為「成功率 100%」。
    /// </summary>
    public static double? CalculateFailureRate(int completed, int failed)
    {
        var total = completed + failed;

        return total <= 0 ? null : failed * 100d / total;
    }

    /// <summary>把一組（開始、結束）時間算成平均耗時。沒有任何完整區間時回 null。</summary>
    public static TimeSpan? AverageDuration(IEnumerable<(DateTime Started, DateTime Completed)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var ticks = ranges
            .Where(range => range.Completed > range.Started)
            .Select(range => (range.Completed - range.Started).Ticks)
            .ToList();

        return ticks.Count == 0 ? null : TimeSpan.FromTicks((long)ticks.Average());
    }

    private static string PointOnCircle(double centerX, double centerY, double radius, double degrees)
    {
        // -90 度是為了讓 0 度落在正上方（一般人讀圓餅圖的起點），而不是數學慣例的正右方。
        var radians = (degrees - 90) * Math.PI / 180;

        return FormatPoint(
            centerX + (radius * Math.Cos(radians)),
            centerY + (radius * Math.Sin(radians)));
    }

    private static string FormatPoint(double x, double y) => $"{Format(x)} {Format(y)}";

    /// <summary>SVG 的數值一律用不變文化格式——遇到以逗號當小數點的地區會產生無效路徑。</summary>
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
