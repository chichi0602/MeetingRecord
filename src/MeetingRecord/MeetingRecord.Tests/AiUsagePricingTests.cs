using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 用量換算金額（0.4.80）。
///
/// <para>
/// 這裡每一筆的共同主題是：**算不出來時必須是 null，不能是 0**。
/// 0 與「真的沒花錢」在畫面上看起來一模一樣，會讓總額靜靜地少報而且完全沒有跡象。
/// </para>
/// </summary>
public sealed class AiUsagePricingTests
{
    private static readonly LlmPricingSettings TextPrice = new()
    {
        InputPerMillionTokens = 0.15m,
        OutputPerMillionTokens = 0.60m,
    };

    private static readonly LlmPricingSettings AudioPrice = new()
    {
        AudioPerMinute = 0.006m,
    };

    #region token 計價

    [Fact]
    public void EstimateTokenCost_ShouldPriceInputAndOutputSeparately()
    {
        // 輸出單價是輸入的 4 倍，兩者一定要分開算。
        // 100 萬輸入 * 0.15 + 50 萬輸出 * 0.60 = 0.15 + 0.30 = 0.45
        var cost = AiUsagePricing.EstimateTokenCost(1_000_000, 500_000, TextPrice);

        Assert.Equal(0.45m, cost);
    }

    [Fact]
    public void EstimateTokenCost_ShouldNotTreatOutputAsInput()
    {
        // 若把輸出誤用輸入單價，下面兩個會算出相同的金額。
        var mostlyInput = AiUsagePricing.EstimateTokenCost(1_000_000, 0, TextPrice);
        var mostlyOutput = AiUsagePricing.EstimateTokenCost(0, 1_000_000, TextPrice);

        Assert.NotEqual(mostlyInput, mostlyOutput);
        Assert.Equal(0.15m, mostlyInput);
        Assert.Equal(0.60m, mostlyOutput);
    }

    [Fact]
    public void EstimateTokenCost_NoPriceConfigured_ShouldReturnNull()
    {
        // ⚠️ 這是整個功能最容易無聲出錯的地方：換了 deployment 卻忘了設單價，
        // 回 0 的話畫面會顯示「本月 $0.00」，看起來像是這個月沒人用。
        Assert.Null(AiUsagePricing.EstimateTokenCost(1_000_000, 500_000, null));
    }

    [Fact]
    public void EstimateTokenCost_PartialPrice_ShouldReturnNull()
    {
        // 只設了輸入單價。算出「只含輸入」的金額比不算更糟——
        // 它看起來像個完整的數字，實際上少了一大半（輸出通常才是大宗）。
        var halfPrice = new LlmPricingSettings { InputPerMillionTokens = 0.15m };

        Assert.Null(AiUsagePricing.EstimateTokenCost(1_000_000, 500_000, halfPrice));
    }

    [Fact]
    public void EstimateTokenCost_NoUsageReported_ShouldReturnNull()
    {
        // 串流被中斷、或 api-version 不支援 include_usage 時，供應商沒回報用量。
        // 這種呼叫**照樣花了錢**，但我們不猜——猜出來的數字會被當成事實。
        Assert.Null(AiUsagePricing.EstimateTokenCost(null, null, TextPrice));
        Assert.Null(AiUsagePricing.EstimateTokenCost(1000, null, TextPrice));
        Assert.Null(AiUsagePricing.EstimateTokenCost(null, 1000, TextPrice));
    }

    [Fact]
    public void EstimateTokenCost_ZeroTokens_ShouldReturnZeroNotNull()
    {
        // 0 token 與「沒回報」是兩回事：前者真的是 0 元。
        Assert.Equal(0m, AiUsagePricing.EstimateTokenCost(0, 0, TextPrice));
    }

    #endregion

    #region 時長計價

    [Fact]
    public void EstimateAudioCost_ShouldPriceProRataPerMinute()
    {
        // 90 秒 = 1.5 分鐘 * 0.006 = 0.009
        var cost = AiUsagePricing.EstimateAudioCost(90, AudioPrice);

        Assert.Equal(0.009m, cost);
    }

    [Fact]
    public void EstimateAudioCost_NoPriceConfigured_ShouldReturnNull()
    {
        Assert.Null(AiUsagePricing.EstimateAudioCost(900, null));
        Assert.Null(AiUsagePricing.EstimateAudioCost(900, TextPrice));
    }

    [Fact]
    public void EstimateAudioCost_NoDuration_ShouldReturnNull()
    {
        // FFmpeg 連 Duration 都拿不到的檔案。轉錄照樣花了錢，但金額留白。
        Assert.Null(AiUsagePricing.EstimateAudioCost(null, AudioPrice));
    }

    [Fact]
    public void EstimateAudioCost_NegativeDuration_ShouldReturnNull()
    {
        // 負秒數會產生負金額，而負金額在圖表上只是看起來「這個月比較省」。
        Assert.Null(AiUsagePricing.EstimateAudioCost(-1, AudioPrice));
    }

    #endregion

    #region 單價表的查找

    [Fact]
    public void PricingLookup_ShouldBeCaseInsensitive()
    {
        // ⚠️ 漏掉 StringComparer.OrdinalIgnoreCase 的後果不是例外，而是「查不到單價」，
        // 於是金額整片留白，看起來像是單價沒填。
        var settings = new LlmSettings();
        settings.Pricing["gpt-4o-mini"] = TextPrice;

        Assert.True(settings.Pricing.TryGetValue("GPT-4O-MINI", out var found));
        Assert.Equal(0.15m, found!.InputPerMillionTokens);
    }

    #endregion

    #region 設定驗證

    [Fact]
    public void Validate_NegativePrice_ShouldFail()
    {
        var settings = new LlmSettings();
        settings.Pricing["gpt-4o-mini"] = new LlmPricingSettings { InputPerMillionTokens = -0.15m };

        var results = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        Assert.Contains(results, x => x.ErrorMessage!.Contains("InputPerMillionTokens", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativePrice_ShouldFailEvenWithoutTranscriptionProvider()
    {
        // ⚠️ Validate 中間有 yield break：單價檢查若放在後面，
        // 「沒有指定 TranscriptionProvider」的部署就永遠檢查不到。
        var settings = new LlmSettings { DefaultProvider = string.Empty, TranscriptionProvider = string.Empty };
        settings.Pricing["gpt-4o-transcribe"] = new LlmPricingSettings { AudioPerMinute = -1m };

        var results = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        Assert.Contains(results, x => x.ErrorMessage!.Contains("AudioPerMinute", StringComparison.Ordinal));
    }

    #endregion
}
