using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>系統健康監控的「測試 AI 連線」（1.0.35）。經 AiCallGateway，記入用量帳本。</summary>
public sealed class AiConnectionTestServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;

    public AiConnectionTestServiceTests()
    {
        connection.Open();
        context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Run_BothDeploymentsReachable_ShouldSucceedAndRecordUsageUnderConnectionTestFeatures()
    {
        var text = new FakeText();
        var transcription = new FakeTranscription();

        var results = await CreateService(Settings(), text, transcription).RunAsync();

        Assert.All(results, x => Assert.True(x.Success, x.Message));
        Assert.Equal(["gpt-5.6-sol", "gpt-transcribe"], results.Select(x => x.Deployment));
        Assert.All(results, x => Assert.Equal("kmengineopenai1.services.ai.azure.com", x.Host));
        Assert.Equal(1, text.Calls);
        Assert.True(transcription.ReceivedBytes > 44, "送出的應該是有內容的 WAV。");

        var features = await context.AiUsageLog.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Feature).ToListAsync();
        Assert.Equal([AiUsageFeature.ConnectionTest, AiUsageFeature.TranscriptionConnectionTest], features);
    }

    [Fact]
    public async Task Run_DeploymentNotFound_ShouldBeClassified()
    {
        var notFound = new AiProviderHttpException(404, AzureDeploymentErrors.Describe(
            "文字生成", 404, "Not Found", """{"error":{"code":"DeploymentNotFound"}}""", "gpt-4o-mini",
            "https://kmengineopenai1.services.ai.azure.com", "LlmSettings:Providers:AzureOpenAI:Model"));

        var result = await CreateService(Settings(), new FakeText { Failure = notFound }, new FakeTranscription()).TestTextGenerationAsync();

        Assert.False(result.Success);
        Assert.StartsWith("部署「gpt-5.6-sol」在 Azure 上不存在。", result.Message);
    }

    [Fact]
    public async Task Run_Unauthorized_ShouldSayKeyProblem()
    {
        var result = await CreateService(Settings(), new FakeText { Failure = new AiProviderHttpException(401, "文字生成 API 回應 401") }, new FakeTranscription())
            .TestTextGenerationAsync();

        Assert.Contains("金鑰錯誤", result.Message);
    }

    /// <summary>沒設部署名稱時不送任何請求，直接說缺哪個鍵。</summary>
    [Fact]
    public async Task Run_NotConfigured_ShouldNotCallAnything()
    {
        var settings = Settings();
        settings.Providers["AzureOpenAI"].Deployment = string.Empty;
        settings.Providers["AzureOpenAI"].TranscriptionDeployment = string.Empty;
        var text = new FakeText();
        var transcription = new FakeTranscription();

        var results = await CreateService(settings, text, transcription).RunAsync();

        Assert.All(results, x => Assert.False(x.Success));
        Assert.Contains("LlmSettings:Providers:AzureOpenAI:Model", results[0].Message);
        Assert.Contains("LlmSettings:Providers:AzureOpenAI:TranscriptionModel", results[1].Message);
        Assert.Equal(0, text.Calls);
        Assert.Equal(0, transcription.ReceivedBytes);
        Assert.False(await context.AiUsageLog.AnyAsync());
    }

    [Fact]
    public void BuildSilentWav_ShouldBeAValidOneSecondMonoWav()
    {
        var wav = AiConnectionTestService.BuildSilentWav(TimeSpan.FromSeconds(1));

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(44 + 32_000, wav.Length);
    }

    /// <summary>計費單位由功能別決定：轉錄類以音訊時長計，其餘以 token 計。</summary>
    [Fact]
    public void Features_ShouldHaveTextsAndBillingUnits()
    {
        Assert.Equal("AI 連線測試", AiUsageFeatureText.Describe(AiUsageFeature.ConnectionTest));
        Assert.Equal("AI 連線測試（轉錄）", AiUsageFeatureText.Describe(AiUsageFeature.TranscriptionConnectionTest));
        Assert.True(AiUsageFeatureText.IsTokenBased(AiUsageFeature.ConnectionTest));
        Assert.False(AiUsageFeatureText.IsTokenBased(AiUsageFeature.TranscriptionConnectionTest));
    }

    /// <summary>IIS 上的實際寫法：部署名稱用 Deployment／TranscriptionDeployment 別名。</summary>
    private static LlmSettings Settings()
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI", TranscriptionProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://kmengineopenai1.services.ai.azure.com",
            ApiKey = "secret-key",
            Deployment = "gpt-5.6-sol",
            TranscriptionDeployment = "gpt-transcribe",
        };
        return settings;
    }

    private AiConnectionTestService CreateService(LlmSettings settings, FakeText text, FakeTranscription transcription)
    {
        var options = Options.Create(settings);
        return new AiConnectionTestService(
            [text],
            [transcription],
            options,
            TestAiServices.CreateGateway(context, options),
            new CurrentUserService(),
            NullLogger<AiConnectionTestService>.Instance);
    }

    private sealed class FakeText : ITextGenerationProvider
    {
        public Exception? Failure { get; init; }

        public int Calls { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-5.6-sol";

        public Task<TextGenerationResult> GenerateAsync(
            string systemPrompt, string userPrompt, Action<string>? onDelta, CancellationToken cancellationToken, IReadOnlyList<PromptImage>? images = null)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult(new TextGenerationResult("OK", null))
                : Task.FromException<TextGenerationResult>(Failure);
        }
    }

    private sealed class FakeTranscription : ITranscriptionProvider
    {
        public long ReceivedBytes { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-transcribe";

        public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await audio.CopyToAsync(buffer, cancellationToken);
            ReceivedBytes = buffer.Length;
            return string.Empty;
        }
    }
}
