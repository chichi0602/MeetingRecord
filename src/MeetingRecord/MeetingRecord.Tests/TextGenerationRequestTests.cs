using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Tests;

/// <summary>
/// 文字生成管線中兩段純邏輯的單元測試：Azure OpenAI 端點位址與回應內容取值。
/// 這裡刻意只測純函式——不打真實 API。
/// </summary>
public sealed class TextGenerationRequestTests
{
    #region Azure OpenAI 端點組裝

    [Theory]
    [InlineData("https://contoso.openai.azure.com/", "https://contoso.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://contoso.openai.azure.com", "https://contoso.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://kmengine.services.ai.azure.com", "https://kmengine.services.ai.azure.com/openai/v1/chat/completions")]
    public void BuildRequestUri_ShouldUseV1PathWithoutApiVersion(string endpoint, string expected)
    {
        // 1.0.28 起走 v1 API：部署名稱在 body 的 model，網址不帶部署名稱也不帶 api-version。
        var uri = AzureOpenAiTextGenerationProvider.BuildRequestUri(endpoint, "gpt-5.6-sol");

        Assert.Equal(expected, uri.ToString());
        Assert.DoesNotContain("api-version", uri.ToString());
    }

    [Theory]
    [InlineData("", "gpt-4o-mini")]
    [InlineData("https://contoso.openai.azure.com/", "")]
    public void BuildRequestUri_ShouldThrow_WhenRequiredPartIsBlank(string endpoint, string deploymentName)
    {
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTextGenerationProvider.BuildRequestUri(endpoint, deploymentName));
    }

    #endregion

    #region 串流片段取值

    [Fact]
    public void ExtractDelta_ShouldReturnFirstChoiceDeltaContent()
    {
        const string payload = """
            {"choices":[{"delta":{"role":"assistant","content":"會議"}}]}
            """;

        Assert.Equal("會議", AzureOpenAiTextGenerationProvider.ExtractDelta(payload));
    }

    [Fact]
    public void ExtractDelta_ShouldPreserveWhitespace()
    {
        // 增量會被逐片接起來，這裡若順手 Trim 就會把字與字之間的空白吃掉。
        const string payload = """
            {"choices":[{"delta":{"content":" and "}}]}
            """;

        Assert.Equal(" and ", AzureOpenAiTextGenerationProvider.ExtractDelta(payload));
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"delta":{}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop"}]}""")]
    [InlineData("{}")]
    public void ExtractDelta_ShouldReturnNull_WhenChunkCarriesNoText(string payload)
    {
        // 沒有文字是常態：第一片只帶內容過濾註記、最後一片只帶 finish_reason。
        Assert.Null(AzureOpenAiTextGenerationProvider.ExtractDelta(payload));
    }

    [Fact]
    public void ExtractDelta_ShouldThrow_WhenChunkIsNotJson()
    {
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTextGenerationProvider.ExtractDelta("<html>gateway error</html>"));
    }

    #endregion
}
