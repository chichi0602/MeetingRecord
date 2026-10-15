using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 「監控與診斷」的 AI 分析入口（0.4.121）。帳本要求涵蓋全部付費呼叫：
/// 成功、失敗、**取消**都要記一筆，而且功能別要對（AI 日誌分析＝5）。
/// </summary>
public sealed class DiagnosticAiServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly RecordingAiCallLogRecorder callLogRecorder = new();
    private readonly FakeAiQuotaService quota = new();

    public DiagnosticAiServiceTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task AnalyzeAsync_Success_ShouldStreamReturnAndRecordUsage()
    {
        var provider = new FakeProvider { Response = "## 總結\n一切正常" };
        var service = CreateService(provider);
        var deltas = new List<string>();

        var result = await service.AnalyzeAsync(AiUsageFeature.LogAnalysis, "system", "user", deltas.Add, CancellationToken.None);

        Assert.Equal("## 總結\n一切正常", result.Content);
        Assert.Equal("gpt-4o-mini", result.ModelName);
        Assert.Equal(result.Content, string.Concat(deltas));
        Assert.Equal(("system", "user"), provider.LastCall);

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageFeature.LogAnalysis, row.Feature);
        Assert.Equal(AiUsageOutcome.Succeeded, row.Outcome);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(7, row.UserId);
    }

    [Fact]
    public async Task AnalyzeAsync_Failure_ShouldRecordFailedAndRethrow()
    {
        var provider = new FakeProvider { Failure = new HttpRequestException("context_length_exceeded") };
        var service = CreateService(provider);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.AnalyzeAsync(AiUsageFeature.LogAnalysis, "s", "u", null, CancellationToken.None));

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageOutcome.Failed, row.Outcome);
        Assert.Contains("context_length_exceeded", row.ErrorMessage);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancelled_ShouldRecordCancelledAndRethrow()
    {
        // 使用者關掉視窗：取消的呼叫照樣花了錢，帳本一定要有這一筆。
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(new FakeProvider { Response = "x" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AnalyzeAsync(AiUsageFeature.LogAnalysis, "s", "u", null, cts.Token));

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageOutcome.Cancelled, row.Outcome);
        Assert.Equal(AiUsageFeature.LogAnalysis, row.Feature);
    }

    [Fact]
    public void IsAvailable_WithoutDefaultProvider_ShouldBeFalseWithReason()
    {
        var service = CreateService(new FakeProvider(), defaultProvider: string.Empty);

        Assert.False(service.IsAvailable);
        Assert.Contains("DefaultProvider", service.UnavailableReason);
    }

    [Fact]
    public void IsAvailable_WithUnknownProvider_ShouldBeFalseWithReason()
    {
        var service = CreateService(new FakeProvider(), defaultProvider: "OpenAI");

        Assert.False(service.IsAvailable);
        Assert.Contains("OpenAI", service.UnavailableReason);
    }

    [Fact]
    public void IsAvailable_WithMatchingProvider_ShouldBeTrue()
    {
        var service = CreateService(new FakeProvider());

        Assert.True(service.IsAvailable);
        Assert.Equal(string.Empty, service.UnavailableReason);
    }

    private DiagnosticAiService CreateService(FakeProvider provider, string defaultProvider = "AzureOpenAI")
    {
        var settings = Options.Create(new LlmSettings { DefaultProvider = defaultProvider });
        var currentUser = new CurrentUserService();
        currentUser.CurrentUser.Id = 7;
        currentUser.CurrentUser.Account = "admin";
        currentUser.CurrentUser.Name = "管理者";

        return new DiagnosticAiService(
            [provider],
            settings,
            TestAiServices.CreateGateway(context, settings, callLogRecorder, quota),
            currentUser,
            NullLogger<DiagnosticAiService>.Instance);
    }

    [Fact]
    public async Task AnalyzeAsync_QuotaReached_ShouldThrowWithoutCallingTheModel()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();
        var provider = new FakeProvider { Response = "x" };

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => CreateService(provider)
            .AnalyzeAsync(AiUsageFeature.LogAnalysis, "system", "user", null, CancellationToken.None));

        Assert.Null(provider.LastCall);
        Assert.False(await context.AiUsageLog.AnyAsync());
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldPassRelatedInfoToTheCallLog()
    {
        await CreateService(new FakeProvider { Response = "x" })
            .AnalyzeAsync(AiUsageFeature.ExceptionAnalysis, "system", "user", null, CancellationToken.None, relatedInfo: "例外紀錄 #3（NullReferenceException）");

        var entry = Assert.Single(callLogRecorder.Entries);
        Assert.Equal("例外紀錄 #3（NullReferenceException）", entry.RelatedInfo);
        Assert.Equal(7, entry.UserId);
    }

    private sealed class FakeProvider : ITextGenerationProvider
    {
        public string Response { get; init; } = string.Empty;

        public Exception? Failure { get; init; }

        public (string System, string User)? LastCall { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-4o-mini";

        public Task<TextGenerationResult> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            Action<string>? onDelta,
            CancellationToken cancellationToken,
            IReadOnlyList<PromptImage>? images = null)
        {
            LastCall = (systemPrompt, userPrompt);
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is not null)
            {
                throw Failure;
            }

            // 拆兩段回報，確認呼叫端拿到的是增量而不是累積字串。
            var midpoint = Response.Length / 2;
            onDelta?.Invoke(Response[..midpoint]);
            onDelta?.Invoke(Response[midpoint..]);

            return Task.FromResult(new TextGenerationResult(Response, new AiTokenUsage(100, 50)));
        }
    }
}
