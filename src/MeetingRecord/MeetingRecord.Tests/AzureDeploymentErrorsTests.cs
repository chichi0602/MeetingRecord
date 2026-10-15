using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// Azure 部署相關的錯誤訊息（1.0.35）。1.0.34 部署到 IIS 時，設定鍵寫錯讓系統送出預設的 gpt-4o-mini，
/// Azure 回 404 DeploymentNotFound，訊息卻看不出送的是哪個部署。
/// </summary>
public sealed class AzureDeploymentErrorsTests
{
    private const string DeploymentNotFoundBody =
        """{"error":{"type":"invalid_request_error","code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}""";

    [Fact]
    public async Task TextGeneration_404DeploymentNotFound_ShouldNameDeploymentHostAndKey_WithoutKey()
    {
        var provider = new AzureOpenAiTextGenerationProvider(
            new StubFactory(HttpStatusCode.NotFound, DeploymentNotFoundBody),
            Options.Create(Settings(model: "gpt-4o-mini")),
            NullLogger<AzureOpenAiTextGenerationProvider>.Instance);

        var ex = await Assert.ThrowsAsync<AiProviderHttpException>(
            () => provider.GenerateAsync("系統", "問題", null, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("找不到 Azure 部署「gpt-4o-mini」", ex.Message);
        Assert.Contains("kmengineopenai1.services.ai.azure.com", ex.Message);
        Assert.Contains("LlmSettings:Providers:AzureOpenAI:Model", ex.Message);
        Assert.Contains("Deployment", ex.Message);
        Assert.DoesNotContain("secret-key", ex.Message);
    }

    [Fact]
    public async Task Transcription_404DeploymentNotFound_ShouldPointToTranscriptionModel()
    {
        var provider = new AzureOpenAiTranscriptionProvider(
            new StubFactory(HttpStatusCode.NotFound, DeploymentNotFoundBody),
            Options.Create(Settings(model: "gpt-5.6-sol", transcriptionModel: "gpt-4o-transcribe")),
            NullLogger<AzureOpenAiTranscriptionProvider>.Instance);

        using var audio = new MemoryStream([1, 2, 3]);
        var ex = await Assert.ThrowsAsync<AiProviderHttpException>(
            () => provider.TranscribeAsync(audio, "a.mp3", CancellationToken.None));

        Assert.Contains("找不到 Azure 部署「gpt-4o-transcribe」", ex.Message);
        Assert.Contains("LlmSettings:Providers:AzureOpenAI:TranscriptionModel", ex.Message);
        Assert.Contains("TranscriptionDeployment", ex.Message);
    }

    /// <summary>1.0.35 起預設值清空、空白不擋啟動：用到時要講清楚缺哪個鍵，而且不送出請求。</summary>
    [Fact]
    public async Task TextGeneration_BlankModel_ShouldExplainMissingKeyWithoutCallingApi()
    {
        var factory = new StubFactory(HttpStatusCode.OK, string.Empty);
        var provider = new AzureOpenAiTextGenerationProvider(
            factory,
            Options.Create(Settings(model: string.Empty)),
            NullLogger<AzureOpenAiTextGenerationProvider>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GenerateAsync("系統", "問題", null, CancellationToken.None));

        Assert.Contains("LlmSettings:Providers:AzureOpenAI:Model", ex.Message);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Describe_OtherErrors_ShouldKeepOriginalShape()
    {
        var message = AzureDeploymentErrors.Describe("文字生成", 429, "Too Many Requests", "slow down", "gpt-5.6-sol", "https://x.openai.azure.com", "LlmSettings:Providers:AzureOpenAI:Model");

        Assert.Equal("文字生成 API 回應 429 Too Many Requests：slow down", message);
    }

    private static LlmSettings Settings(string model, string transcriptionModel = "")
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI", TranscriptionProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://kmengineopenai1.services.ai.azure.com",
            ApiKey = "secret-key",
            Model = model,
            TranscriptionModel = transcriptionModel,
        };
        return settings;
    }

    private sealed class StubFactory(HttpStatusCode status, string body) : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this, status, body), disposeHandler: true);

        private sealed class Handler(StubFactory owner, HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Calls++;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
