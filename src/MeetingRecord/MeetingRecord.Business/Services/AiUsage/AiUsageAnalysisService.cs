using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.Models.Systems;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 「AI 用量分析」頁的讀取層。
///
/// <para>
/// ⚠️ <b>金額一律撈進記憶體再加總，絕不在 SQL 端 SUM 或 ORDER BY。</b>
/// SQLite 把 <c>decimal</c> 存成 TEXT，排序是字典序（<c>"10.0" &lt; "9.0"</c>）、
/// 比較大小同理。這與 <see cref="DashboardService"/>「一次撈回來再於記憶體分組」
/// 的既有做法一致，資料量也在同一個量級。
/// </para>
///
/// <para>
/// 不做團隊過濾：這一頁是管理員限定（權限檢查在 View），而成本帳本套上部分過濾會讓
/// 「依使用者分佈」的加總對不上總金額。⚠️ 反過來說，若哪天把這個權限勾給非管理員角色，
/// 那個人就會看到**全部**團隊的用量與人名。
/// </para>
///
/// <para>
/// 刻意不做快取，理由同 <see cref="DashboardService"/>：用量分析本來就該顯示當下的數字。
/// </para>
/// </summary>
public class AiUsageAnalysisService
{
    /// <summary>分佈圖最多顯示幾項，其餘併成「其他」。</summary>
    private const int TopCount = 8;

    private readonly BackendDBContext context;
    private readonly ExchangeRateCache exchangeRates;
    private readonly IOptions<ExchangeRateSettings> exchangeRateSettings;
    private readonly ILogger<AiUsageAnalysisService> logger;

    public AiUsageAnalysisService(
        BackendDBContext context,
        ExchangeRateCache exchangeRates,
        IOptions<ExchangeRateSettings> exchangeRateSettings,
        ILogger<AiUsageAnalysisService> logger)
    {
        this.context = context;
        this.exchangeRates = exchangeRates;
        this.exchangeRateSettings = exchangeRateSettings;
        this.logger = logger;
    }

    /// <summary>
    /// 這一頁要用哪一種幣別顯示（0.4.88）。
    ///
    /// <para>
    /// ⚠️ 換算關閉時**整頁原樣退回定價幣別**。「有匯率的列顯示台幣、沒有的顯示美金」
    /// 會讓總額變成兩種幣別相加——那不是降級，是錯的數字。
    /// </para>
    /// </summary>
    private CostView BuildCostView(string sourceCurrency)
        => exchangeRateSettings.Value.Enabled
            ? new CostView(true, sourceCurrency, exchangeRateSettings.Value.TargetCurrency)
            : new CostView(false, sourceCurrency, sourceCurrency);

    /// <summary>
    /// 整頁的摘要（本月至今）。
    ///
    /// <para>
    /// <paramref name="feature"/> 不為 null 時，卡片、曲線、模型與使用者分佈、各種提示筆數
    /// **全部只算這一個功能**（0.4.91）。在那之前這個下拉只接到明細表，
    /// 卡片永遠是全部功能——使用者選了下拉卻看不到任何數字變化。
    /// </para>
    ///
    /// <para>
    /// 0.4.91 之前還收一個 <c>trendDays</c>：舊的曲線是「最近 N 天」。曲線改成本月累計之後，
    /// 摘要已經沒有任何東西依賴期間，那個參數只剩明細表在用。
    /// </para>
    /// </summary>
    public async Task<AiUsageSummary> GetSummaryAsync(
        AiUsageFeature? feature = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var (currentFrom, previousFrom, previousToExclusive) = AiUsageMetrics.BuildMonthToDateRanges(now);

        // ⚠️ 統計起始日刻意**不套功能篩選**：它講的是「帳本從哪天開始記」，
        //    不是「這個功能第一次被用是哪天」。套了的話選「待辦擷取」會顯示一個比較晚的起始日，
        //    使用者會以為在那之前的紀錄遺失了。
        var startedAt = await context.AiUsageLog.AsNoTracking()
            .OrderBy(x => x.OccurredAt)
            .Select(x => (DateTime?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        // 卡片、分佈與曲線都是「本月至今」對「上月同期」，上月起點就是需要的最早資料。
        var source = context.AiUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= previousFrom);

        // Feature 是 int 列舉欄位，在 SQL 端比對是安全的——
        // 與 EstimatedCost／ExchangeRate 那種存成 TEXT 的 decimal 不同。
        if (feature is { } selected)
        {
            source = source.Where(x => x.Feature == selected);
        }

        var facts = await source
            .Select(x => new UsageFact(
                x.OccurredAt,
                x.Feature,
                x.Outcome,
                x.Model,
                x.UserName,
                x.InputTokens,
                x.OutputTokens,
                x.AudioSeconds,
                x.EstimatedCost,
                x.Currency,
                x.ExchangeRate))
            .ToListAsync(cancellationToken);

        var currency = facts.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "USD";

        var thisMonth = facts.Where(x => x.OccurredAt >= currentFrom).ToList();
        var lastMonthSamePeriod = facts
            .Where(x => x.OccurredAt >= previousFrom && x.OccurredAt < previousToExclusive)
            .ToList();

        var view = BuildCostView(currency);

        // ⭐ 月比月一律用**定價幣別**算，不可以用換算後的金額。
        //    台幣的月比月 = 用量變化 × 匯率變化，匯率動 2% 會在卡片上顯示成「花費增加 2%」，
        //    而畫面上沒有任何線索指出那是匯率造成的。比較的必須是實際花掉的錢。
        var thisMonthCost = SumCost(thisMonth);
        var lastMonthCost = SumCost(lastMonthSamePeriod);

        return new AiUsageSummary(
            startedAt,
            BuildCards(thisMonth, thisMonthCost, lastMonthCost, view, feature),
            BuildCumulativeCost(thisMonth, lastMonthSamePeriod, now, view),
            BuildSlices(thisMonth.GroupBy(x => AiUsageFeatureText.Describe(x.Feature)), view),
            BuildSlices(thisMonth.GroupBy(x => string.IsNullOrWhiteSpace(x.Model) ? "（未知）" : x.Model), view),
            BuildSlices(thisMonth.GroupBy(x => x.UserName ?? "（未記錄）"), view),
            view.Format(view.Sum(thisMonth)),
            thisMonth.Count(x => x.EstimatedCost is null && x.Outcome == AiUsageOutcome.Succeeded),
            thisMonth.Count(x => x.Outcome != AiUsageOutcome.Succeeded),
            // 有金額卻換不出來的才算——沒單價的那些已經由 UnpricedCallCount 提示過了。
            view.Convert ? thisMonth.Count(x => x.EstimatedCost is not null && x.ExchangeRate is null) : 0,
            BuildExchangeRateNote(view));
    }

    /// <summary>
    /// 最近呼叫明細（分頁）。
    ///
    /// <para>
    /// ⚠️ <c>Take</c> **無條件套用**。這是刻意與其他七支 service 不同的寫法——它們把 Take
    /// 包在 <c>if (dataRequest.Take != 0)</c> 裡而呼叫端一律傳 0，等於從來沒有分頁過。
    /// 帳本是全系統唯一會持續成長的表，不分頁遲早會把頁面拖垮。
    /// </para>
    /// </summary>
    public async Task<AiUsagePagedResult> GetRecentCallsAsync(
        AiUsageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = context.AiUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= query.From && x.OccurredAt < query.ToExclusive);

        if (query.Feature is { } feature)
        {
            source = source.Where(x => x.Feature == feature);
        }

        var totalCount = await source.CountAsync(cancellationToken);

        var page = Math.Max(query.CurrentPage, 1);
        var size = Math.Clamp(query.PageSize, 1, 200);

        // ⚠️ ThenByDescending(Id) 不可省：一趟分段摘要會在同一秒內寫進十幾列，
        //    只用 OccurredAt 排序時 SQLite 不保證穩定，翻頁會看到同一列出現兩次、另一列消失。
        // ⚠️ 絕對不可 OrderBy(EstimatedCost)——decimal 在 SQLite 是 TEXT，那是字典序。
        var rows = await source
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new
            {
                x.Id,
                x.OccurredAt,
                x.Feature,
                x.Outcome,
                x.Model,
                x.UserName,
                x.MeetingId,
                x.ProjectId,
                x.InputTokens,
                x.OutputTokens,
                x.AudioSeconds,
                x.IsAudioDurationEstimated,
                x.EstimatedCost,
                x.Currency,
                x.ExchangeRate,
                x.ErrorMessage,
            })
            .ToListAsync(cancellationToken);

        var rowView = BuildCostView(
            rows.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "USD");

        return new AiUsagePagedResult(
            totalCount,
            [.. rows.Select(x => new AiUsageRow(
                x.Id,
                x.OccurredAt,
                x.Feature,
                AiUsageFeatureText.Describe(x.Feature),
                x.Outcome,
                AiUsageOutcomeText.Describe(x.Outcome),
                string.IsNullOrWhiteSpace(x.Model) ? "（未知）" : x.Model,
                x.UserName,
                DescribeTarget(x.MeetingId, x.ProjectId),
                DescribeUsage(x.Feature, x.InputTokens, x.OutputTokens, x.AudioSeconds, x.IsAudioDurationEstimated),
                rowView.Format(rowView.Of(x.EstimatedCost, x.ExchangeRate)),
                x.ErrorMessage))]);
    }

    private static string DescribeTarget(int? meetingId, int? projectId)
        => meetingId is { } meeting ? $"會議 #{meeting}"
            : projectId is { } project ? $"專案 #{project}"
            : "—";

    private static string DescribeUsage(
        AiUsageFeature feature,
        int? inputTokens,
        int? outputTokens,
        double? audioSeconds,
        bool isEstimated)
    {
        if (!AiUsageFeatureText.IsTokenBased(feature))
        {
            return audioSeconds is { } seconds
                ? AiUsageMetrics.FormatAudio(seconds) + (isEstimated ? "（推估）" : string.Empty)
                : "—";
        }

        // 沒回報用量（失敗、取消、或供應商不支援）一律顯示「—」，不要假裝是 0。
        return inputTokens is { } input && outputTokens is { } output
            ? $"{AiUsageMetrics.FormatTokens(input)} / {AiUsageMetrics.FormatTokens(output)}"
            : "—";
    }

    private static decimal SumCost(IEnumerable<UsageFact> facts)
        // 在記憶體加總——見類別註解的 SQLite decimal 說明。
        => facts.Sum(x => x.EstimatedCost ?? 0m);

    private static IReadOnlyList<StatCardItem> BuildCards(
        IReadOnlyList<UsageFact> thisMonth,
        decimal thisMonthCost,
        decimal lastMonthCost,
        CostView view,
        AiUsageFeature? feature)
    {
        // ⭐ thisMonthCost／lastMonthCost 是**定價幣別**的金額，刻意不換算——見 GetSummaryAsync 的說明。
        var changeRate = AiUsageMetrics.CalculateChangeRate(thisMonthCost, lastMonthCost);
        var failedCount = thisMonth.Count(x => x.Outcome != AiUsageOutcome.Succeeded);

        var inputTokens = thisMonth.Sum(x => (long)(x.InputTokens ?? 0));
        var outputTokens = thisMonth.Sum(x => (long)(x.OutputTokens ?? 0));
        var audioSeconds = thisMonth.Sum(x => x.AudioSeconds ?? 0d);

        return
        [
            new StatCardItem(
                "本月估算金額",
                view.Format(view.Sum(thisMonth)),
                changeRate is { } rate
                    ? $"與上月同期相比 {(rate >= 0 ? "+" : string.Empty)}{rate:0.#}%"
                    : "上月同期無資料",
                changeRate > 0 ? ChartTone.Warning : ChartTone.Neutral),

            // ⚠️ 選了特定功能時，不用那個單位計費的卡片顯示「—」而不是 0。
            //    「語音轉錄的 token 0 / 0」數字沒錯，但會讓人以為這個月沒用，
            //    其實是這個功能根本不以 token 計費（沿用本專案「算不出來不顯示 0」的慣例）。
            feature is { } tokenCheck && !AiUsageFeatureText.IsTokenBased(tokenCheck)
                ? new StatCardItem("本月 token", "—", "語音轉錄以音訊時長計費")
                : new StatCardItem(
                    "本月 token",
                    $"{AiUsageMetrics.FormatTokens(inputTokens)} / {AiUsageMetrics.FormatTokens(outputTokens)}",
                    "輸入 / 輸出"),

            feature is { } audioCheck && AiUsageFeatureText.IsTokenBased(audioCheck)
                ? new StatCardItem("本月轉錄時長", "—", "這個功能以 token 計費")
                : new StatCardItem(
                    "本月轉錄時長",
                    AiUsageMetrics.FormatAudio(audioSeconds),
                    "送進語音辨識的音訊"),

            new StatCardItem(
                "本月呼叫次數",
                thisMonth.Count.ToString(),
                failedCount > 0 ? $"含 {failedCount} 次失敗或取消" : "全部成功",
                failedCount > 0 ? ChartTone.Danger : ChartTone.Success),
        ];
    }

    /// <summary>
    /// 「本月累計 vs 上月同期累計」（0.4.91）。取代舊的「文字生成 vs 語音轉錄每日金額」——
    /// 那個二分法跟功能下拉、功能別圓餅講的是同一件事，而且資料少時整條幾乎都是 0。
    ///
    /// <para>
    /// 金額用**顯示幣別**（跟整頁其他地方一樣）。卡片上的月比月百分比則刻意用定價幣別算，
    /// 所以兩條線最後一點的比例與卡片百分比可能差匯率那 1～2%，這是刻意的，見 GetSummaryAsync。
    /// </para>
    /// </summary>
    private static IReadOnlyList<TrendPoint> BuildCumulativeCost(
        IReadOnlyList<UsageFact> thisMonth,
        IReadOnlyList<UsageFact> lastMonthSamePeriod,
        DateTime now,
        CostView view)
    {
        // ⚠️ 用 DateOnly.FromDateTime 直接取日期，**不套 ToLocalTime()**：
        //    SQLite 讀回來的 Kind 是 Unspecified，套了會整批位移 8 小時
        //    （DashboardMetrics 已經記過這個坑）。
        // 算不出金額的列（沒單價或沒匯率）當 0 計入——與卡片總額的加總規則一致，
        // 曲線最後一點才會等於卡片上的數字。
        return AiUsageMetrics.BuildMonthToDateCumulative(
            thisMonth.Select(x => (DateOnly.FromDateTime(x.OccurredAt), view.Of(x) ?? 0m)),
            lastMonthSamePeriod.Select(x => (DateOnly.FromDateTime(x.OccurredAt), view.Of(x) ?? 0m)),
            DateOnly.FromDateTime(now));
    }

    /// <summary>
    /// 把分組結果做成圖表切片：<c>Value</c> 用「分」負責幾何，<c>Display</c> 給正確的金額文字。
    /// </summary>
    private static IReadOnlyList<ChartSlice> BuildSlices(
        IEnumerable<IGrouping<string, UsageFact>> groups,
        CostView view)
    {
        var ordered = groups
            .Select(group => (
                Label: group.Key,
                Cost: view.Sum(group),
                // ⚠️ 整組都算不出金額時，加總會是 0，顯示成「NT$ 0」——那與「真的沒花錢」
                //    看起來一模一樣。這一組要標成「未設定單價」而不是零元。
                //    0.4.88 起「算不出來」多了一種原因：有單價但沒有匯率。
                HasPrice: group.Any(x => view.Of(x) is not null)))
            .OrderByDescending(x => x.Cost)
            .ToList();

        var top = ordered.Take(TopCount).ToList();

        // 超出的併成「其他」，而不是默默不顯示——加總對不上總額會讓人以為數字有問題。
        var rest = ordered.Skip(TopCount).Sum(x => x.Cost);
        if (rest > 0)
        {
            top.Add(("其他", rest, true));
        }

        return
        [
            .. top.Select(x => new ChartSlice(
                x.Label,
                AiUsageMetrics.ToChartCents(x.Cost),
                x.HasPrice ? ChartTone.Neutral : ChartTone.Warning,
                x.HasPrice ? view.Format(x.Cost) : "未設定單價或無匯率")),
        ];
    }

    /// <summary>目前生效的匯率說明，給畫面顯示。未啟用換算或還沒抓到匯率時回 null。</summary>
    private string? BuildExchangeRateNote(CostView view)
    {
        if (!view.Convert || exchangeRates.Current is not { } rate)
        {
            return null;
        }

        return $"匯率 1 {rate.BaseCurrency} = {rate.Rate:0.0000} {rate.TargetCurrency}";
    }

    /// <summary>
    /// 這一頁的金額怎麼算、怎麼印（0.4.88）。把「要不要換算」集中在一個地方，
    /// 免得五個顯示點（卡片、折線、三張分佈圖、明細表）各自判斷而漏掉其中一個。
    /// </summary>
    /// <param name="Convert">是否換算成 <paramref name="DisplayCurrency"/>。</param>
    /// <param name="SourceCurrency">定價幣別（帳本上的 <c>Currency</c>）。</param>
    /// <param name="DisplayCurrency">實際顯示用的幣別。</param>
    private sealed record CostView(bool Convert, string SourceCurrency, string DisplayCurrency)
    {
        /// <summary>單列的顯示金額。要換算但該列沒有匯率時回 null——不可當成 0 或原值。</summary>
        public decimal? Of(decimal? estimatedCost, decimal? exchangeRate)
            => Convert ? AiUsageExchange.ToTargetCurrency(estimatedCost, exchangeRate) : estimatedCost;

        public decimal? Of(UsageFact fact) => Of(fact.EstimatedCost, fact.ExchangeRate);

        /// <summary>在記憶體加總——見類別註解的 SQLite decimal 說明。換不出來的列不計入。</summary>
        public decimal Sum(IEnumerable<UsageFact> facts) => facts.Sum(x => Of(x) ?? 0m);

        public string Format(decimal? amount)
            => Convert
                ? AiUsageMetrics.FormatConverted(amount, DisplayCurrency)
                : AiUsageMetrics.FormatAmount(amount, SourceCurrency);
    }

    /// <summary>查詢投影。撈欄位而不是整個實體——帳本是全系統列數最多的表。</summary>
    private sealed record UsageFact(
        DateTime OccurredAt,
        AiUsageFeature Feature,
        AiUsageOutcome Outcome,
        string Model,
        string? UserName,
        int? InputTokens,
        int? OutputTokens,
        double? AudioSeconds,
        decimal? EstimatedCost,
        string? Currency,
        decimal? ExchangeRate);
}
