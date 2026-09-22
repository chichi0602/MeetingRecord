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
    /// 把每日兩組數列攤成稠密的 <see cref="TrendPoint"/>。
    ///
    /// <para>
    /// ⚠️ 迭代的是**桶**而不是**有資料的日子**：沒有任何呼叫的那一天也必須在圖上佔一格，
    /// 否則折線會把兩個相隔一週的點畫成相鄰，看起來像是天天都在花錢。
    /// </para>
    /// </summary>
    /// <param name="items">每一筆的發生日與兩條線各自的量。</param>
    public static IReadOnlyList<TrendPoint> BuildDailySeries(
        IEnumerable<(DateOnly Day, int SeriesOne, int SeriesTwo)> items,
        DateOnly today,
        int days)
    {
        ArgumentNullException.ThrowIfNull(items);

        var buckets = DashboardMetrics.BuildDayBuckets(today, days);
        var labels = DashboardMetrics.BuildDayLabels(buckets);

        var one = new Dictionary<DateOnly, int>();
        var two = new Dictionary<DateOnly, int>();

        foreach (var (day, seriesOne, seriesTwo) in items)
        {
            one[day] = one.GetValueOrDefault(day) + seriesOne;
            two[day] = two.GetValueOrDefault(day) + seriesTwo;
        }

        var points = new List<TrendPoint>(buckets.Count);
        for (var index = 0; index < buckets.Count; index++)
        {
            var day = buckets[index];
            points.Add(new TrendPoint(labels[index], one.GetValueOrDefault(day), two.GetValueOrDefault(day)));
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
