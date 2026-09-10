using System.Globalization;

namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>
/// 儀表板用到的純計算：日期分桶、軸標籤稀疏化、圓餅幾何、百分比與耗時描述。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker</c>、<c>ChatContextBuilder</c>、
/// <c>TranscriptionNoiseFilter</c> 同一個慣例，不碰 IO、不碰資料庫。
/// </summary>
public static class DashboardMetrics
{
    /// <summary>軸線上最多顯示幾個日期標籤。</summary>
    private const int MaxVisibleLabels = 7;

    /// <summary>
    /// 產生「最近 N 天」的日期桶，由舊到新，**含今天**（所以起點是 today − (N-1)）。
    ///
    /// <para>
    /// **沒有資料的日子也一定會在**——趨勢圖若只畫有資料的日子，
    /// 時間軸會被壓縮成不等距，相隔兩週的兩個點看起來會像連續兩天。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DateOnly> BuildDayBuckets(DateOnly today, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);

        return [.. Enumerable
            .Range(0, days)
            .Select(offset => today.AddDays(offset - (days - 1)))];
    }

    /// <summary>
    /// 日期桶的顯示標籤。
    ///
    /// <para>
    /// 刻意不做「跨年才帶年份」那套：範圍最長 90 天，不可能出現重複的 MM/dd，
    /// 跨年時「10/18 … 01/15」的先後一望即知，帶上年份只會讓軸線更擠。
    /// </para>
    /// </summary>
    public static string DescribeDay(DateOnly day)
        => day.ToString("MM/dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 日期桶的軸標籤，與 <paramref name="days"/> **等長**，沒被選到的位置是空字串。
    ///
    /// <para>
    /// 90 個 MM/dd 全畫會疊成一團，所以每 step 天才標一個。**空字串仍要佔一個位置**：
    /// 畫面把標籤排成等寬的 flex 槽位，少一個槽位，其後所有可見標籤都會與資料點錯位。
    /// </para>
    /// <para>
    /// 由後往前數（<c>count - 1 - index</c>），所以**最後一天永遠有標籤**——
    /// 那是讀者定位用的錨點。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildDayLabels(IReadOnlyList<DateOnly> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        // Math.Max(1, …) 不是防禦性冗贅：天數少於 MaxVisibleLabels 時
        // 整數除法會得到 0，接下來的 % step 就是除以零。
        var step = Math.Max(1, (int)Math.Ceiling(days.Count / (double)MaxVisibleLabels));

        return [.. days.Select((day, index) => (days.Count - 1 - index) % step == 0
            ? DescribeDay(day)
            : string.Empty)];
    }

    /// <summary>
    /// 「最近 N 天」的雙線趨勢：每天新增幾場會議、完成幾份會議紀錄。
    /// </summary>
    public static IReadOnlyList<TrendPoint> BuildDailyTrend(
        IEnumerable<(DateTime Created, DateTime? DraftCompleted)> meetings,
        DateOnly today,
        int days)
    {
        ArgumentNullException.ThrowIfNull(meetings);

        var buckets = BuildDayBuckets(today, days);
        var labels = BuildDayLabels(buckets);

        // 先分組成字典再逐桶查表；逐桶 Count 等於把同一份清單掃 2N 遍。
        //
        // 時間戳一律當本地時間直接取日期部分——這些欄位都是 DateTime.Now 寫入的，
        // 從 SQLite 讀回來 Kind 是 Unspecified，套 ToLocalTime() 會被當成 UTC
        // 而整批位移 8 小時，把下午建立的會議算到隔天。
        var items = meetings.ToList();

        var created = items
            .GroupBy(x => DateOnly.FromDateTime(x.Created))
            .ToDictionary(group => group.Key, group => group.Count());

        var completed = items
            .Where(x => x.DraftCompleted is not null)
            .GroupBy(x => DateOnly.FromDateTime(x.DraftCompleted!.Value))
            .ToDictionary(group => group.Key, group => group.Count());

        // 迭代 buckets 而不是迭代 groups：稠密性（沒資料的日子也要在）與
        // 「窗外的舊資料不被堆到第 0 桶」兩件事，都由這個方向自動成立。
        return [.. buckets.Select((day, index) => new TrendPoint(
            labels[index],
            created.GetValueOrDefault(day),
            completed.GetValueOrDefault(day)))];
    }

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
