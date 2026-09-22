using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 「AI 用量分析」整頁的資料。
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
    string? ErrorMessage);

/// <summary>明細表的查詢條件。</summary>
/// <remarks>
/// 刻意**不重用** <c>DataRequest</c>：那組型別帶著 Search／Sort／Category／Team 過濾，
/// 這一頁一個都不需要；而且重用它很容易把那七支 service 的分頁缺陷一起複製過來
/// （<c>Take</c> 被包在 <c>if (dataRequest.Take != 0)</c> 裡，於是從來沒真的分頁）。
/// </remarks>
public sealed record AiUsageQuery(
    DateTime From,
    DateTime ToExclusive,
    AiUsageFeature? Feature,
    int CurrentPage,
    int PageSize);

/// <summary>明細表的一頁。</summary>
public sealed record AiUsagePagedResult(int TotalCount, IReadOnlyList<AiUsageRow> Rows);
