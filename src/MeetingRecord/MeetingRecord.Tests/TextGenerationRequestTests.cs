using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Tests;

/// <summary>
/// 文字生成管線中兩段純邏輯的單元測試：Azure OpenAI 端點位址與回應內容取值。
/// 這裡刻意只測純函式——不打真實 API。
/// </summary>
public sealed class TextGenerationRequestTests
{
    #region Azure OpenAI 端點組裝

    [Fact]
    public void BuildRequestUri_ShouldComposeDeploymentAndApiVersion()
    {
        var uri = AzureOpenAiTextGenerationProvider.BuildRequestUri(
            "https://contoso.openai.azure.com/",
            "gpt-4o-mini",
            "2024-10-21");

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/deployments/gpt-4o-mini/chat/completions?api-version=2024-10-21",
            uri.ToString());
    }

    [Fact]
    public void BuildRequestUri_ShouldNotProduceDoubleSlash_WhenEndpointHasNoTrailingSlash()
    {
        var uri = AzureOpenAiTextGenerationProvider.BuildRequestUri(
            "https://contoso.openai.azure.com",
            "gpt-4o-mini",
            "2024-10-21");

        Assert.DoesNotContain("azure.com//", uri.ToString());
    }

    [Theory]
    [InlineData("", "gpt-4o-mini", "2024-10-21")]
    [InlineData("https://contoso.openai.azure.com/", "", "2024-10-21")]
    [InlineData("https://contoso.openai.azure.com/", "gpt-4o-mini", "")]
    public void BuildRequestUri_ShouldThrow_WhenRequiredPartIsBlank(
        string endpoint,
        string deploymentName,
        string apiVersion)
    {
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTextGenerationProvider.BuildRequestUri(endpoint, deploymentName, apiVersion));
    }

    #endregion

    #region 回應內容取值

    [Fact]
    public void ExtractContent_ShouldReturnFirstChoiceMessage()
    {
        const string body = """
            {"choices":[{"message":{"role":"assistant","content":"  會議紀錄內容  "}}]}
            """;

        Assert.Equal("會議紀錄內容", AzureOpenAiTextGenerationProvider.ExtractContent(body));
    }

    [Fact]
    public void ExtractContent_ShouldThrow_WhenResponseIsNotJson()
    {
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTextGenerationProvider.ExtractContent("<html>gateway error</html>"));
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]
    [InlineData("{}")]
    public void ExtractContent_ShouldThrow_WhenNoContentIsReturned(string body)
    {
        // 內容過濾或模型無輸出時會走到這裡，必須是失敗而不是靜靜寫入空草稿。
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTextGenerationProvider.ExtractContent(body));
    }

    #endregion
}
