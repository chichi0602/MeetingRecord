using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 把用量換算成估算金額。抽成純函式以便單元測試——與 <c>TranscriptChunker</c>、
/// <c>MediaDurationParser</c> 同一個慣例，不碰 IO、不碰資料庫。
///
/// <para>
/// ⚠️ **算不出來時一律回 null，絕不回 0。** 0 與「真的沒花錢」在畫面上看起來一模一樣，
/// 會讓總額靜靜地少報；null 則會在頁面上顯示成「—（未設定單價）」，促使人去把設定補上。
/// </para>
///
/// <para>
/// 金額是在**寫入帳本的當下**算好存起來的，不是讀取時才算。單價會變，而這頁最主要的用途
/// 是「本月 vs 上月」——若歷史被新單價重算，那個比較就失去意義。
/// </para>
/// </summary>
public static class AiUsagePricing
{
    /// <summary>每百萬 token 的單價換算基數。</summary>
    private const decimal TokensPerPriceUnit = 1_000_000m;

    private const decimal SecondsPerMinute = 60m;

    /// <summary>
    /// 文字生成的估算金額。
    ///
    /// <para>
    /// 輸入與輸出**必須分開計價**（輸出通常是輸入的 3～4 倍）。
    /// 任一邊缺用量或缺單價就整筆回 null——半套的金額比沒有金額更糟，
    /// 它看起來像個完整的數字，實際上少算了一大半。
    /// </para>
    /// </summary>
    public static decimal? EstimateTokenCost(int? inputTokens, int? outputTokens, LlmPricingSettings? price)
    {
        if (price is null)
        {
            return null;
        }

        if (inputTokens is not { } input || outputTokens is not { } output)
        {
            // 供應商沒回報用量（串流被中斷、api-version 不支援 include_usage）。
            return null;
        }

        if (price.InputPerMillionTokens is not { } inputPrice || price.OutputPerMillionTokens is not { } outputPrice)
        {
            return null;
        }

        return (input / TokensPerPriceUnit * inputPrice) + (output / TokensPerPriceUnit * outputPrice);
    }

    /// <summary>
    /// 語音轉錄的估算金額。
    ///
    /// <para>
    /// 以秒按比例換算。實際上 Azure 可能以分鐘進位計費，所以這是**估算**——
    /// 頁面上要說明實際帳單可能略高。
    /// </para>
    /// </summary>
    public static decimal? EstimateAudioCost(double? audioSeconds, LlmPricingSettings? price)
    {
        if (price?.AudioPerMinute is not { } perMinute)
        {
            return null;
        }

        if (audioSeconds is not { } seconds || seconds < 0)
        {
            return null;
        }

        return (decimal)seconds / SecondsPerMinute * perMinute;
    }
}
