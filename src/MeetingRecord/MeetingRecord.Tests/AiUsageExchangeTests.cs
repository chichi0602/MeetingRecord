using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 金額換算（0.4.88）。
///
/// <para>
/// 這裡只有兩條規則，但它們刻意相反，而且兩條都很容易寫錯：
/// <b>算不出來回 null</b>（不可回 0、更不可把缺席的匯率當成 1），
/// 但 <b>金額真的是 0 時回 0</b>（「這筆沒花到錢」是合法結果）。
/// </para>
/// </summary>
public sealed class AiUsageExchangeTests
{
    private const decimal Rate = 31.863639m;

    [Fact]
    public void ToTargetCurrency_ShouldMultiply()
    {
        Assert.Equal(Rate, AiUsageExchange.ToTargetCurrency(1m, Rate));
    }

    [Fact]
    public void ToTargetCurrency_ShouldNotRoundEarly()
    {
        // ⭐ 一次 AI 問答大約是 USD 0.000045。提前四捨五入會讓整張明細表變成 0，
        // 格式化才是決定要顯示幾位小數的地方。
        var result = AiUsageExchange.ToTargetCurrency(0.000045m, Rate);

        Assert.NotNull(result);
        Assert.True(result > 0m, $"換算結果不該被提前捨成 0，實際是 {result}。");
        Assert.Equal(0.000045m * Rate, result);
    }

    [Fact]
    public void ToTargetCurrency_ZeroAmount_ShouldStayZero()
    {
        // ⭐ 與下面那組刻意相反：「真的沒花錢」是合法值，不可以變成 null（畫面會顯示「—」）。
        Assert.Equal(0m, AiUsageExchange.ToTargetCurrency(0m, Rate));
    }

    [Theory]
    [InlineData(null, 31.863639)]   // 沒單價
    [InlineData(1.5, null)]         // ⭐ 沒匯率——當成 1 會讓金額少報三十幾倍且看不出來
    [InlineData(1.5, 0.0)]          // 0 匯率會讓所有金額變成 0，看起來像「這個月比較省」
    [InlineData(1.5, -31.8)]
    public void ToTargetCurrency_Unusable_ShouldReturnNull(double? amount, double? rate)
    {
        var result = AiUsageExchange.ToTargetCurrency((decimal?)amount, (decimal?)rate);

        Assert.Null(result);
    }
}

/// <summary>
/// 匯率設定的啟動驗證（0.4.88）。比照 <c>LlmSettingsTests</c>：
/// 設定打錯字如果不在啟動時擋下來，系統會完全正常啟動、然後永遠抓不到匯率，
/// 只在 log 裡留一行 warning——那是最難被發現的一種錯。
/// </summary>
public sealed class ExchangeRateSettingsTests
{
    private static List<string> Validate(ExchangeRateSettings settings)
        => [.. settings
            .Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings))
            .Select(x => x.ErrorMessage ?? string.Empty)];

    [Fact]
    public void Defaults_ShouldBeValid()
    {
        Assert.Empty(Validate(new ExchangeRateSettings()));
    }

    [Fact]
    public void Disabled_ShouldSkipEveryOtherCheck()
    {
        // 關掉之後其他鍵一個都不會被讀，沒必要因為它們擋住啟動。
        var settings = new ExchangeRateSettings
        {
            Enabled = false,
            SourceUrl = "not a url",
            TargetCurrency = "nope",
            RefreshIntervalHours = 0,
            FallbackRate = -1m,
        };

        Assert.Empty(Validate(settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/rates")]
    [InlineData("/v6/latest/USD")]
    public void BadSourceUrl_ShouldBeRejected(string url)
    {
        Assert.NotEmpty(Validate(new ExchangeRateSettings { SourceUrl = url }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("TWD ")]     // ⭐ 尾隨空白：不擋的話會完全正常啟動然後永遠查不到匯率
    [InlineData("TW")]
    [InlineData("TWDX")]
    [InlineData("T1D")]
    public void BadTargetCurrency_ShouldBeRejected(string currency)
    {
        Assert.NotEmpty(Validate(new ExchangeRateSettings { TargetCurrency = currency }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-31.8)]
    public void NonPositiveFallbackRate_ShouldBeRejected(double rate)
    {
        // 0 匯率會讓所有台幣金額變成 NT$0，在圖表上只是看起來「這個月比較省」。
        Assert.NotEmpty(Validate(new ExchangeRateSettings { FallbackRate = (decimal)rate }));
    }

    [Fact]
    public void NullFallbackRate_ShouldBeAllowed()
    {
        // 留空是刻意的預設：沒有匯率就不換算，而不是拿一個看起來合理的數字硬湊。
        Assert.Empty(Validate(new ExchangeRateSettings { FallbackRate = null }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(200)]
    public void BadRefreshInterval_ShouldBeRejected(int hours)
    {
        Assert.NotEmpty(Validate(new ExchangeRateSettings { RefreshIntervalHours = hours }));
    }
}
