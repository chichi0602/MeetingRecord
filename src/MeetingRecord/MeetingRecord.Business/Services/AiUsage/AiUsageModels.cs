using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 「Token 用量」頁上方「本月概況」的資料（1.0.30 前名為「AI 用量分析」，當時是整頁的資料）。
///
/// <para>
/// 圖表型別一律沿用 <see cref="MeetingRecord.Business.Services.Dashboard"/> 的
/// <see cref="ChartSlice"/>／<see cref="TrendPoint"/>／<see cref="StatCardItem"/>——
/// 造一套平行型別只會逼所有圖表元件各支援兩種輸入。
/// </para>
/// </summary>
/// <param name="StartedAt">帳本第一筆的時間；沒有任何紀錄時為 null。畫面要明示統計起始日。</param>
/// <param name="UnpricedCallCount">
/// 模型未設定單價、因而金額未納入統計的呼叫筆數。
/// **大於 0 時畫面一定要提示**——否則使用者會以為那些呼叫真的免費。
/// </param>
/// <param name="NoRateCallCount">
/// 有金額但沒有匯率、因而未納入換算後統計的呼叫筆數（0.4.88）。
/// 典型來源是 0.4.88 之前的舊紀錄，或抓不到匯率的那段期間。
/// **大於 0 時畫面一定要提示**，理由與 <paramref name="UnpricedCallCount"/> 相同。
/// </param>
/// <param name="CumulativeCost">
/// 本月 1 日到今天的累計金額（分），兩條線是「本月」與「上月同期」（0.4.91）。
/// 最後一點就是卡片上「本月估算金額」與它比較的上月同期金額。
/// </param>
/// <param name="ExchangeRateNote">
/// 目前生效的匯率說明，例如「匯率 1 USD = 31.8636 TWD」。未啟用換算或沒有匯率時為 null。
/// </param>
public sealed record AiUsageSummary(
    DateTime? StartedAt,
    IReadOnlyList<StatCardItem> Cards,
    IReadOnlyList<TrendPoint> CumulativeCost,
    IReadOnlyList<ChartSlice> ByFeature,
    IReadOnlyList<ChartSlice> ByModel,
    IReadOnlyList<ChartSlice> ByUser,
    string TotalCostDisplay,
    int UnpricedCallCount,
    int FailedCallCount,
    int NoRateCallCount,
    string? ExchangeRateNote);

/// <summary>明細表的一列。</summary>
/// <param name="UsageText">token 或音訊時長，依功能別擇一。</param>
/// <param name="CostText">估算金額；單價未設定時是「—」。</param>
public sealed record AiUsageRow(
    int Id,
    DateTime OccurredAt,
    AiUsageFeature Feature,
    string FeatureText,
    AiUsageOutcome Outcome,
    string OutcomeText,
    string Model,
    string? UserName,
    string? Target,
    string UsageText,
    string CostText,
    string? ErrorMessage,
    Guid? CallId = null);

/// <summary>
/// Token 用量頁的篩選條件（1.0.31）。明細、合計、每日、分組、CSV、PDF 共用同一組條件。
/// ⚠️ 只比對整數、字串與日期欄位——金額欄位（decimal 存成 TEXT）一律不進 SQL 條件。
/// </summary>
/// <param name="From">起始時間（含）；null 表示不限。</param>
/// <param name="ToExclusive">結束時間（不含）；畫面的「結束日」傳隔天零時，當天才不會被切掉。</param>
/// <param name="UserId">使用者 Id；null 表示不限。</param>
/// <param name="Model">模型名稱（完全相同）；null 或空字串表示不限。</param>
public sealed record AiUsageFilter(
    DateTime? From = null,
    DateTime? ToExclusive = null,
    AiUsageFeature? Feature = null,
    int? UserId = null,
    string? Model = null,
    AiUsageOutcome? Outcome = null);

/// <summary>明細表的查詢條件。</summary>
/// <remarks>
/// 刻意**不重用** <c>DataRequest</c>：那組型別帶著 Search／Sort／Category／Team 過濾，
/// 這一頁一個都不需要；而且重用它很容易把那七支 service 的分頁缺陷一起複製過來
/// （<c>Take</c> 被包在 <c>if (dataRequest.Take != 0)</c> 裡，於是從來沒真的分頁）。
/// </remarks>
public sealed record AiUsageQuery(
    AiUsageFilter Filter,
    int CurrentPage,
    int PageSize,
    bool SortAscending = false);

/// <summary>明細表的一頁。</summary>
public sealed record AiUsagePagedResult(int TotalCount, IReadOnlyList<AiUsageRow> Rows);

/// <summary>
/// 篩選範圍的統計（1.0.31）：合計、每日、依使用者／功能／模型分組，以及「圖表」頁籤的三組切片。
/// 金額一律是**顯示幣別**（換算開啟時為新台幣），在記憶體加總。
/// </summary>
/// <param name="Cost">換算得出金額的列加總；算不出來的列不計入（筆數見 <paramref name="UnpricedCallCount"/>、<paramref name="NoRateCallCount"/>）。</param>
/// <param name="IsDailyFilled">每日是否補齊了沒有資料的日子（區間不超過 <see cref="AiUsageAnalysisService.MaxFilledDays"/> 天才補）。</param>
public sealed record AiUsageReport(
    int CallCount,
    int FailedCallCount,
    long InputTokens,
    long OutputTokens,
    double AudioSeconds,
    decimal Cost,
    string CostText,
    int UnpricedCallCount,
    int NoRateCallCount,
    IReadOnlyList<AiUsageDailyRow> Daily,
    bool IsDailyFilled,
    IReadOnlyList<AiUsageGroupRow> ByUser,
    IReadOnlyList<AiUsageGroupRow> ByFeature,
    IReadOnlyList<AiUsageGroupRow> ByModel,
    IReadOnlyList<ChartSlice> FeatureSlices,
    IReadOnlyList<ChartSlice> ModelSlices,
    IReadOnlyList<ChartSlice> UserSlices);

/// <summary>分組統計的一列（依使用者、功能或模型）。</summary>
/// <param name="NotCountedCount">金額算不出來（沒單價或沒匯率）而未計入的筆數；失敗且沒有金額的不算。</param>
public sealed record AiUsageGroupRow(
    string Key,
    int CallCount,
    int FailedCount,
    long InputTokens,
    long OutputTokens,
    double AudioSeconds,
    decimal Cost,
    string CostText,
    int NotCountedCount);

/// <summary>每日統計的一列。</summary>
public sealed record AiUsageDailyRow(
    DateOnly Date,
    int CallCount,
    long InputTokens,
    long OutputTokens,
    double AudioSeconds,
    decimal Cost,
    string CostText,
    int NotCountedCount);

/// <summary>篩選下拉的選項：帳本裡實際出現過的使用者（取最近一次的名稱）與模型。</summary>
public sealed record AiUsageFilterOptions(
    IReadOnlyList<(int UserId, string UserName)> Users,
    IReadOnlyList<string> Models);

/// <summary>
/// 單筆用量的完整內容（1.0.31）：明細窗與 CSV 共用。單價與匯率都是**呼叫當下的快照**，改設定不影響。
/// </summary>
/// <param name="ConvertedCost">換算後金額；換算關閉時等於原幣金額，沒單價或沒匯率時為 null。</param>
/// <param name="CostText">顯示用的金額文字（與明細表同一套規則）。</param>
public sealed record AiUsageDetail(
    int Id,
    Guid? CallId,
    DateTime OccurredAt,
    AiUsageFeature Feature,
    AiUsageOutcome Outcome,
    string Provider,
    string Model,
    int? UserId,
    string? UserName,
    int? MeetingId,
    int? ProjectId,
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens,
    double? AudioSeconds,
    bool IsAudioDurationEstimated,
    decimal? EstimatedCost,
    string? Currency,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    decimal? AudioPricePerMinute,
    decimal? ExchangeRate,
    string? ConvertedCurrency,
    decimal? ConvertedCost,
    string CostText,
    string? ErrorMessage)
{
    public string FeatureText => AiUsageFeatureText.Describe(Feature);

    public string OutcomeText => AiUsageOutcomeText.Describe(Outcome);

    /// <summary>對象：會議或專案編號。</summary>
    public string TargetText => MeetingId is { } meeting ? $"會議 #{meeting}"
        : ProjectId is { } project ? $"專案 #{project}"
        : "—";
}
