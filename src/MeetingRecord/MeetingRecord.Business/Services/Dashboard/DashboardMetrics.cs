using System.Globalization;

using MeetingRecord.Dtos.Dashboards;
using MeetingRecord.Share.Helpers;

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
    /// 啟用中、但沒有任何會議紀錄用過的範本數。
    ///
    /// <para>
    /// ⚠️ <b>比對的是名稱不是外鍵。</b><c>Meeting.DraftPromptTemplateName</c> 是生成當下的
    /// 名稱快照（讓範本被刪掉之後仍看得出當初用了什麼），所以<b>範本改名之後，
    /// 舊紀錄記的是舊名，這個範本會被算成「從沒用過」</b>。這是快照設計的必然結果，不是 bug。
    /// </para>
    ///
    /// </summary>
    public static int CountUnusedEnabledTemplates(
        IEnumerable<(string Name, bool IsEnabled)> templates,
        IEnumerable<string?> usedTemplateNames)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(usedTemplateNames);

        // 忽略大小寫比對，與 TagStringHelper 全站的去重慣例一致。
        var used = usedTemplateNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return templates.Count(template =>
            template.IsEnabled && !used.Contains((template.Name ?? string.Empty).Trim()));
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
        => ChartGeometry.BuildArcPath(centerX, centerY, radius, startDegrees, sweepDegrees); // 1.1.12：搬到 Share，WASM 的圓餅圖也要用

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
}
