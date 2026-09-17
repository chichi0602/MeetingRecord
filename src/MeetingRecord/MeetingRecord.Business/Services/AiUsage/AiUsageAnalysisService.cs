using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
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
    private readonly ILogger<AiUsageAnalysisService> logger;

    public AiUsageAnalysisService(BackendDBContext context, ILogger<AiUsageAnalysisService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    /// <summary>整頁的摘要。<paramref name="trendDays"/> 是趨勢圖的天數。</summary>
    public async Task<AiUsageSummary> GetSummaryAsync(int trendDays, CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var (currentFrom, previousFrom, previousToExclusive) = AiUsageMetrics.BuildMonthToDateRanges(now);

        var startedAt = await context.AiUsageLog.AsNoTracking()
            .OrderBy(x => x.OccurredAt)
            .Select(x => (DateTime?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        // 趨勢圖與「上月同期」都要涵蓋，所以一次撈到兩者較早的那個起點。
        var trendFrom = now.Date.AddDays(-(trendDays - 1));
        var from = previousFrom < trendFrom ? previousFrom : trendFrom;

        var facts = await context.AiUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= from)
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
                x.Currency))
            .ToListAsync(cancellationToken);

        var currency = facts.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "USD";

        var thisMonth = facts.Where(x => x.OccurredAt >= currentFrom).ToList();
        var lastMonthSamePeriod = facts
            .Where(x => x.OccurredAt >= previousFrom && x.OccurredAt < previousToExclusive)
            .ToList();

        var thisMonthCost = SumCost(thisMonth);
        var lastMonthCost = SumCost(lastMonthSamePeriod);

        return new AiUsageSummary(
            startedAt,
            BuildCards(thisMonth, thisMonthCost, lastMonthCost, currency),
            BuildDailyCost(facts, now, trendDays),
            BuildSlices(thisMonth.GroupBy(x => AiUsageFeatureText.Describe(x.Feature)), currency),
            BuildSlices(thisMonth.GroupBy(x => string.IsNullOrWhiteSpace(x.Model) ? "（未知）" : x.Model), currency),
            BuildSlices(thisMonth.GroupBy(x => x.UserName ?? "（未記錄）"), currency),
            CostSeriesOneLabel: "文字生成",
            CostSeriesTwoLabel: "語音轉錄",
            AiUsageMetrics.FormatAmount(thisMonthCost, currency),
            thisMonth.Count(x => x.EstimatedCost is null && x.Outcome == AiUsageOutcome.Succeeded),
            thisMonth.Count(x => x.Outcome != AiUsageOutcome.Succeeded));
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
                x.ErrorMessage,
            })
            .ToListAsync(cancellationToken);

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
                AiUsageMetrics.FormatAmount(x.EstimatedCost, x.Currency),
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
        string currency)
    {
        var changeRate = AiUsageMetrics.CalculateChangeRate(thisMonthCost, lastMonthCost);
        var failedCount = thisMonth.Count(x => x.Outcome != AiUsageOutcome.Succeeded);

        var inputTokens = thisMonth.Sum(x => (long)(x.InputTokens ?? 0));
        var outputTokens = thisMonth.Sum(x => (long)(x.OutputTokens ?? 0));
        var audioSeconds = thisMonth.Sum(x => x.AudioSeconds ?? 0d);

        return
        [
            new StatCardItem(
                "本月估算金額",
                AiUsageMetrics.FormatAmount(thisMonthCost, currency),
                changeRate is { } rate
                    ? $"與上月同期相比 {(rate >= 0 ? "+" : string.Empty)}{rate:0.#}%"
                    : "上月同期無資料",
                changeRate > 0 ? ChartTone.Warning : ChartTone.Neutral),

            new StatCardItem(
                "本月 token",
                $"{AiUsageMetrics.FormatTokens(inputTokens)} / {AiUsageMetrics.FormatTokens(outputTokens)}",
                "輸入 / 輸出"),

            new StatCardItem(
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

    /// <summary>每日金額趨勢，兩條線分別是文字生成與語音轉錄。</summary>
    private static IReadOnlyList<TrendPoint> BuildDailyCost(IReadOnlyList<UsageFact> facts, DateTime now, int days)
    {
        // ⚠️ 用 DateOnly.FromDateTime 直接取日期，**不套 ToLocalTime()**：
        //    SQLite 讀回來的 Kind 是 Unspecified，套了會整批位移 8 小時
        //    （DashboardMetrics 已經記過這個坑）。
        var items = facts.Select(x => (
            Day: DateOnly.FromDateTime(x.OccurredAt),
            SeriesOne: AiUsageFeatureText.IsTokenBased(x.Feature)
                ? AiUsageMetrics.ToChartCents(x.EstimatedCost ?? 0m)
                : 0,
            SeriesTwo: AiUsageFeatureText.IsTokenBased(x.Feature)
                ? 0
                : AiUsageMetrics.ToChartCents(x.EstimatedCost ?? 0m)));

        return AiUsageMetrics.BuildDailySeries(items, DateOnly.FromDateTime(now), days);
    }

    /// <summary>
    /// 把分組結果做成圖表切片：<c>Value</c> 用「分」負責幾何，<c>Display</c> 給正確的金額文字。
    /// </summary>
    private static IReadOnlyList<ChartSlice> BuildSlices(
        IEnumerable<IGrouping<string, UsageFact>> groups,
        string currency)
    {
        var ordered = groups
            .Select(group => (
                Label: group.Key,
                Cost: SumCost(group),
                // ⚠️ 整組都沒有單價時，加總會是 0，顯示成「USD 0」——那與「真的沒花錢」
                //    看起來一模一樣。這一組要標成「未設定單價」而不是零元。
                HasPrice: group.Any(x => x.EstimatedCost is not null)))
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
                x.HasPrice ? AiUsageMetrics.FormatAmount(x.Cost, currency) : "未設定單價")),
        ];
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
        string? Currency);
}
