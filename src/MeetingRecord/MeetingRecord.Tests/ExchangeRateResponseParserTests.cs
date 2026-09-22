using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Tests;

/// <summary>
/// 匯率回應的解析（0.4.88）。
///
/// <para>
/// ⚠️ 這裡的成功樣本是**實際打 open.er-api.com 回來的原文**（只留下幾個幣別），
/// 不是想像出來的格式——與 <c>AzureOpenAiUsageParsingTests</c> 同一個理由：
/// 自己編的樣本只會驗證自己的想像。
/// </para>
///
/// <para>
/// 這組測試守的是一條線：<b>任何不如預期都回 null，絕不回 0、絕不擲例外。</b>
/// 0 匯率會讓所有台幣金額變成 NT$0，在圖表上只是看起來「這個月比較省」；
/// 而擲例外會讓背景服務為了一個匯率而中斷。
/// </para>
/// </summary>
public sealed class ExchangeRateResponseParserTests
{
    /// <summary>實跑 https://open.er-api.com/v6/latest/USD 的回應（幣別只留四個）。</summary>
    private const string RealSample = """
        {"result":"success","provider":"https://www.exchangerate-api.com","base_code":"USD",
         "time_last_update_utc":"Fri, 18 Sep 2026 00:02:31 +0000",
         "rates":{"USD":1,"EUR":0.870624,"JPY":155.83621,"TWD":31.863639}}
        """;

    #region 正常情況

    [Fact]
    public void Parse_RealSample_ShouldReadTheRate()
    {
        var snapshot = ExchangeRateResponseParser.Parse(RealSample, "USD", "TWD");

        Assert.NotNull(snapshot);
        Assert.Equal(31.863639m, snapshot.Rate);
        Assert.Equal("USD", snapshot.BaseCurrency);
        Assert.Equal("TWD", snapshot.TargetCurrency);
    }

    [Fact]
    public void Parse_ShouldMatchCurrencyCodeCaseInsensitively()
    {
        // ⚠️ JsonElement.TryGetProperty 是大小寫敏感的，設定檔卻可能寫成小寫。
        // 不自己處理的話會完全正常啟動、然後永遠抓不到匯率。
        var snapshot = ExchangeRateResponseParser.Parse(RealSample, "usd", "twd");

        Assert.NotNull(snapshot);
        Assert.Equal(31.863639m, snapshot.Rate);

        // 存進帳本的一律正規化成大寫，否則同一種幣別會在畫面上出現兩種寫法。
        Assert.Equal("USD", snapshot.BaseCurrency);
        Assert.Equal("TWD", snapshot.TargetCurrency);
    }

    [Fact]
    public void Parse_ShouldReadAnotherCurrency()
    {
        Assert.Equal(155.83621m, ExchangeRateResponseParser.Parse(RealSample, "USD", "JPY")?.Rate);
    }

    #endregion

    #region 來源幣別對不上

    [Fact]
    public void Parse_BaseCurrencyMismatch_ShouldReturnNull()
    {
        // ⭐ 設定裡的 SourceUrl 結尾寫著幣別（.../latest/USD）。有人把它改成別的、
        // 卻沒改 LlmSettings.Currency 時，金額會安靜地錯好幾 %。這一條就是擋這個。
        Assert.Null(ExchangeRateResponseParser.Parse(RealSample, "EUR", "TWD"));
    }

    #endregion

    #region 壞掉的回應

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("<html><body>503</body></html>")]
    [InlineData("[1,2,3]")]
    public void Parse_Garbage_ShouldReturnNullWithoutThrowing(string? json)
    {
        // 對方回 HTML 錯誤頁、連線被截斷、CDN 擋下來——全都是可預期的狀況，不是程式錯誤。
        Assert.Null(ExchangeRateResponseParser.Parse(json, "USD", "TWD"));
    }

    [Theory]
    // result 不是 success：此時 rates 可能整個不在，也可能是上一次的舊值
    [InlineData("""{"result":"error","error-type":"unsupported-code","base_code":"USD","rates":{"TWD":31.8}}""")]
    // 缺 result
    [InlineData("""{"base_code":"USD","rates":{"TWD":31.8}}""")]
    // 缺 base_code
    [InlineData("""{"result":"success","rates":{"TWD":31.8}}""")]
    // 缺 rates
    [InlineData("""{"result":"success","base_code":"USD"}""")]
    // rates 不是物件
    [InlineData("""{"result":"success","base_code":"USD","rates":"nope"}""")]
    // rates 裡沒有目標幣別
    [InlineData("""{"result":"success","base_code":"USD","rates":{"EUR":0.87}}""")]
    public void Parse_UnexpectedShape_ShouldReturnNull(string json)
    {
        Assert.Null(ExchangeRateResponseParser.Parse(json, "USD", "TWD"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-31.8)]
    public void Parse_NonPositiveRate_ShouldReturnNull(double rate)
    {
        // ⭐ 回 0 的話整頁金額會變成 NT$0，而畫面上看起來完全正常。
        //
        // 樣本用佔位符再替換：JSON 的大括號與差補字串的 {{ }} 會打架，
        // 而 JSON 裡到處是雙引號，串接寫法可讀性很差。
        var json = """{"result":"success","base_code":"USD","rates":{"TWD":RATE}}"""
            .Replace("RATE", rate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Null(ExchangeRateResponseParser.Parse(json, "USD", "TWD"));
    }

    [Fact]
    public void Parse_RateAsString_ShouldReturnNull()
    {
        // 對方換了格式時，「猜」比「不換算」危險得多。
        var json = """{"result":"success","base_code":"USD","rates":{"TWD":"31.8"}}""";

        Assert.Null(ExchangeRateResponseParser.Parse(json, "USD", "TWD"));
    }

    #endregion

    #region 參數缺漏

    [Theory]
    [InlineData(null, "TWD")]
    [InlineData("USD", null)]
    [InlineData("", "TWD")]
    [InlineData("USD", "  ")]
    public void Parse_MissingArguments_ShouldReturnNull(string? baseCurrency, string? target)
    {
        Assert.Null(ExchangeRateResponseParser.Parse(RealSample, baseCurrency, target));
    }

    #endregion
}
