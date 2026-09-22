namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 把估算金額換算成顯示幣別（0.4.88）。抽成純函式以便單元測試——
/// 與 <see cref="AiUsagePricing"/>、<see cref="AiUsageMetrics"/> 同一個慣例。
///
/// <para>
/// ⚠️ 換算用的是**每一列自己存的匯率**（<c>AiUsageLog.ExchangeRate</c>，寫入當下的快照），
/// 不是今天的匯率。用今天的匯率重算歷史，今天匯率一動去年的帳就整批跟著變，
/// 而這頁最主要的用途正是「本月 vs 上月」。
/// </para>
/// </summary>
public static class AiUsageExchange
{
    /// <summary>
    /// 換算成目標幣別。
    ///
    /// <para>
    /// ⚠️ <b>算不出來一律回 null，絕不回 0，也絕不把缺席的匯率當成 1。</b>
    /// 當成 1 會讓台幣金額少報三十幾倍，而畫面上完全看不出來（沿用 <see cref="AiUsagePricing"/> 的規則）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 但金額**真的是 0** 時要回 0 而不是 null——「這筆沒花到錢」是合法結果，
    /// 與「算不出來」是兩回事。這兩條規則刻意相反。
    /// </para>
    /// </summary>
    public static decimal? ToTargetCurrency(decimal? amount, decimal? rate)
    {
        if (amount is not { } value || rate is not { } factor || factor <= 0)
        {
            return null;
        }

        return value * factor;
    }
}
