using System.Globalization;
using MeetingRecord.Business.Services.Dashboard;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 用量數字的純函式。不碰 IO、不碰資料庫——與 <see cref="DashboardMetrics"/>、
/// <c>TranscriptChunker</c>、<c>MediaDurationParser</c> 同一個慣例。
///
/// <para>
/// 日期分桶與軸標籤稀疏化一律**委派給 <see cref="DashboardMetrics"/>**：那兩件事的邊界
/// （沒有資料的日子也要在圖上、空字串的槽位仍要佔位）已經在那裡處理好了，
/// 複製一份必然會有一邊先壞掉。
/// </para>
/// </summary>
public static class AiUsageMetrics
{
    /// <summary>金額換算成「分」——圖表的幾何只吃 int。</summary>
    private const decimal CentsPerUnit = 100m;

    /// <summary>
    /// 「本月累計 vs 上月同期累計」的雙線（0.4.91）。X 軸是本月 1 日到今天，每一點是到當天為止的累計，
    /// 所以**最後一點就是卡片上「本月估算金額」與它比較的那個上月同期金額**——這條曲線是那張卡的趨勢版。
    ///
    /// <para>
    /// ⚠️ **累加的是原始金額，最後才換成「分」。** 每筆先換成分再加會把小額呼叫全部吃掉：
    /// 一次 AI 問答大約 NT$0.0014，換成分是 0.14、四捨五入成 0——一百次問答加起來仍然是 0。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **上個月比較短時延續月底值。** 3/31 對上 2 月時，2 月只有 28 天，第 29～31 天的
    /// 「上月同期」維持 2/28 的累計，而不是掉回 0。這與 <see cref="BuildMonthToDateRanges"/>
    /// 把上月迄日夾到最後一天的規則一致，兩邊必須對得上，否則曲線最後一點會與卡片數字不符。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **只畫到今天、不畫到月底。** 今天之後沒有資料，畫出來不是「看起來停止花錢」（延續）
    /// 就是「看起來掉到 0」（補零），兩種都是錯的。
    /// </para>
    /// </summary>
    /// <param name="thisMonth">本月每一筆的發生日與顯示金額（尚未換成分）。</param>
    /// <param name="lastMonth">上月同期每一筆的發生日與顯示金額。</param>
    /// <param name="today">今天。決定點數（＝今天是幾號）。</param>
    public static IReadOnlyList<TrendPoint> BuildMonthToDateCumulative(
        IEnumerable<(DateOnly Day, decimal Amount)> thisMonth,
        IEnumerable<(DateOnly Day, decimal Amount)> lastMonth,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(thisMonth);
        ArgumentNullException.ThrowIfNull(lastMonth);

        // 本月 1 日 ～ 今天。沿用 DashboardMetrics 的分桶與軸標籤稀疏化，理由見類別註解。
        var buckets = DashboardMetrics.BuildDayBuckets(today, today.Day);
        var labels = DashboardMetrics.BuildDayLabels(buckets);

        // DateOnly.AddMonths 會把日期夾到上月最後一天（3/31 → 2/28），年月正是我們要的。
        var previous = today.AddMonths(-1);
        var daysInPrevious = DateTime.DaysInMonth(previous.Year, previous.Month);

        var thisByDay = new decimal[today.Day + 1];
        foreach (var (day, amount) in thisMonth)
        {
            // 不在本月 1 日～今天的一律忽略，不要讓呼叫端傳錯範圍時畫出超出今天的點。
            if (day.Year == today.Year && day.Month == today.Month && day.Day <= today.Day)
            {
                thisByDay[day.Day] += amount;
            }
        }

        var lastByDay = new decimal[daysInPrevious + 1];
        foreach (var (day, amount) in lastMonth)
        {
            if (day.Year == previous.Year && day.Month == previous.Month)
            {
                lastByDay[day.Day] += amount;
            }
        }

        var points = new List<TrendPoint>(buckets.Count);
        var thisRunning = 0m;
        var lastRunning = 0m;

        for (var dayOfMonth = 1; dayOfMonth <= today.Day; dayOfMonth++)
        {
            thisRunning += thisByDay[dayOfMonth];

            // 上個月沒有這一天時不再累加，維持月底值（見方法註解）。
            if (dayOfMonth <= daysInPrevious)
            {
                lastRunning += lastByDay[dayOfMonth];
            }

            points.Add(new TrendPoint(labels[dayOfMonth - 1], ToChartCents(thisRunning), ToChartCents(lastRunning)));
        }

        return points;
    }

    /// <summary>
    /// 「本月至今」與「上月同期」的區間。
    ///
    /// <para>
    /// ⚠️ 比較的必須是**上月同期**而不是上月整月。每月 3 號拿 3 天比 31 天，
    /// 卡片上會顯示「-89%」，看起來像系統壞了。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 上個月可能沒有今天這個日子（3/31 的上個月是 2 月）。那時取上個月的最後一天，
    /// 而不是讓 <see cref="DateTime"/> 拋例外或滑到下個月。
    /// </para>
    /// </summary>
    /// <returns>本月起、上月同期起、上月同期迄（不含）。</returns>
    public static (DateTime CurrentFrom, DateTime PreviousFrom, DateTime PreviousToExclusive) BuildMonthToDateRanges(
        DateTime now)
    {
        var currentFrom = new DateTime(now.Year, now.Month, 1, 0, 0, 0, now.Kind);
        var previousFrom = currentFrom.AddMonths(-1);

        // 上個月的同一個「第幾天」。上個月天數較少時夾到最後一天。
        var daysInPreviousMonth = DateTime.DaysInMonth(previousFrom.Year, previousFrom.Month);
        var dayOfMonth = Math.Min(now.Day, daysInPreviousMonth);

        var previousSameDay = new DateTime(
            previousFrom.Year, previousFrom.Month, dayOfMonth,
            now.Hour, now.Minute, now.Second, now.Kind);

        return (currentFrom, previousFrom, previousSameDay);
    }

    /// <summary>
    /// 與上期相比的變化率。
    ///
    /// <para>
    /// ⚠️ 上期為 0 時回 <c>null</c>（畫面顯示「上月同期無資料」），**不是 0% 也不是無限大**。
    /// 從零成長的百分比沒有意義，硬算只會印出一個荒謬的數字。
    /// </para>
    /// </summary>
    public static double? CalculateChangeRate(decimal current, decimal previous)
        => previous == 0 ? null : (double)((current - previous) / previous * 100m);

    /// <summary>
    /// 金額換算成「分」供圖表的幾何使用。負數與溢位都夾住。
    ///
    /// <para>
    /// ⚠️ 上界要**在乘以 100 之前**判斷：先乘再比的話，很大的 decimal 會在乘法那一步
    /// 就拋 OverflowException，根本輪不到夾住。
    /// </para>
    /// </summary>
    public static int ToChartCents(decimal amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        if (amount >= int.MaxValue / CentsPerUnit)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(amount * CentsPerUnit, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 金額的顯示文字。單價沒設定（null）時是「—」而不是「$0.00」——
    /// 後者與「真的沒花錢」看起來一模一樣。
    /// </summary>
    public static string FormatAmount(decimal? amount, string? currency)
    {
        if (amount is not { } value)
        {
            return "—";
        }

        // 金額很小（一次問答可能是 0.000045）時，兩位小數會全部變成 0.00。
        var text = value >= 1m
            ? value.ToString("0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.000000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

        return string.IsNullOrWhiteSpace(text) ? "0" : $"{currency} {text}".Trim();
    }

    /// <summary>
    /// 換算後金額的顯示文字（0.4.88）。null（沒單價或沒匯率）時是「—」而不是「NT$ 0」——
    /// 後者與「真的沒花錢」看起來一模一樣。
    ///
    /// <para>
    /// ⚠️ 金額很小時不可四捨五入成 NT$0.00：一次 AI 問答大約是 NT$0.0014，
    /// 兩位小數會讓整張明細表看起來全部免費。
    /// </para>
    /// </summary>
    public static string FormatConverted(decimal? amount, string? currency)
    {
        if (amount is not { } value)
        {
            return "—";
        }

        var text = value >= 1m
            ? value.ToString("#,##0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.0000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "0";
        }

        // 台幣用習慣的符號，其他幣別就印代碼——不去猜每一種幣別的符號。
        return string.Equals(currency, "TWD", StringComparison.OrdinalIgnoreCase)
            ? $"NT$ {text}"
            : $"{currency} {text}".Trim();
    }

    /// <summary>token 數的顯示文字：上千縮寫成 K／M，清單才不會被一串數字撐爆。</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>音訊秒數的顯示文字。</summary>
    public static string FormatAudio(double seconds)
    {
        if (seconds <= 0)
        {
            return "0 分鐘";
        }

        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小時"
            : $"{span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} 分鐘";
    }
}
