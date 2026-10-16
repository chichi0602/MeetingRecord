using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.HelpAssistant;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 使用說明 AI 助理（1.0.43）。提示詞組法是純函式；服務本身要確認走 gateway（記帳、受上限管制）、
/// 功能別正確，以及「只根據使用說明回答、用繁體」這兩條核心指示有送出去。
/// </summary>
public sealed class HelpAssistantTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly RecordingAiCallLogRecorder callLogRecorder = new();
    private readonly FakeAiQuotaService quota = new();

    public HelpAssistantTests()
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

    // ───────── 提示詞 ─────────

    [Fact]
    public void BuildUserPrompt_ShouldWrapManualAndEndWithQuestion()
    {
        var prompt = HelpAssistantPromptBuilder.BuildUserPrompt("## 上傳影音檔\n按新增。", [], "怎麼上傳？");

        Assert.Contains("<使用說明>\n## 上傳影音檔\n按新增。\n</使用說明>", prompt.ReplaceLineEndings("\n"));
        Assert.EndsWith("使用者的問題：怎麼上傳？", prompt.TrimEnd());
        Assert.DoesNotContain("<先前的對話>", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ShouldOnlyKeepRecentTurns()
    {
        // 每一輪都會跟著整份說明再送一次，帶太多會越問越貴。
        var history = Enumerable.Range(1, 5).Select(i => new HelpAssistantTurn($"問題{i}", $"答案{i}")).ToList();

        var prompt = HelpAssistantPromptBuilder.BuildUserPrompt("說明", history, "再問");

        Assert.DoesNotContain("問題1", prompt);
        Assert.DoesNotContain("問題2", prompt);
        Assert.Contains("問題3", prompt);
        Assert.Contains("答案5", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ShouldTruncateLongQuestionAndHistory()
    {
        var longText = new string('字', 5000);

        var prompt = HelpAssistantPromptBuilder.BuildUserPrompt("說明", [new HelpAssistantTurn(longText, longText)], longText);

        Assert.DoesNotContain(new string('字', HelpAssistantPromptBuilder.MaxHistoryMessageLength + 1), prompt);
        Assert.Contains("…（以下省略）", prompt);
        Assert.True(prompt.Length < 5000 * 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildUserPrompt_BlankQuestion_ShouldThrow(string question)
        => Assert.ThrowsAny<ArgumentException>(() => HelpAssistantPromptBuilder.BuildUserPrompt("說明", [], question));

    [Fact]
    public void SystemPrompt_ShouldRequireManualOnlyAndTraditionalChinese()
    {
        Assert.Contains("只根據 <使用說明>", HelpAssistantPromptBuilder.SystemPrompt);
        Assert.Contains("繁體中文", HelpAssistantPromptBuilder.SystemPrompt);
        Assert.Contains("使用說明沒有寫到", HelpAssistantPromptBuilder.SystemPrompt);
    }

    // ───────── 服務 ─────────

    [Fact]
    public async Task AskAsync_ShouldStreamAndRecordAsHelpAssistant()
    {
        var provider = new FakeProvider { Response = "到「會議紀錄」頁按「歸屬到專案」。" };
        var deltas = new List<string>();

        var answer = await CreateService(provider).AskAsync(
            "說明全文", [], "怎麼歸屬到專案？", deltas.Add, CancellationToken.None, threadKey: "help:abc");

        Assert.Equal("到「會議紀錄」頁按「歸屬到專案」。", answer.Content);
        Assert.Equal(answer.Content, string.Concat(deltas));
        Assert.Equal(HelpAssistantPromptBuilder.SystemPrompt, provider.LastCall!.Value.System);
        Assert.Contains("說明全文", provider.LastCall!.Value.User);

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageFeature.HelpAssistant, row.Feature);
        Assert.Equal(AiUsageOutcome.Succeeded, row.Outcome);

        var entry = Assert.Single(callLogRecorder.Entries);
        Assert.Equal("help:abc", entry.ThreadKey);
        Assert.StartsWith("使用說明提問：", entry.RelatedInfo);
    }

    [Fact]
    public async Task AskAsync_QuotaReached_ShouldThrowWithoutCallingTheModel()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();
        var provider = new FakeProvider { Response = "x" };

        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => CreateService(provider).AskAsync("說明", [], "問題", null, CancellationToken.None));

        Assert.Null(provider.LastCall);
        Assert.False(await context.AiUsageLog.AnyAsync());
    }

    [Fact]
    public async Task AskAsync_Cancelled_ShouldRecordCancelled()
    {
        // 使用者按「停止」：取消的呼叫照樣花了錢，帳本要有這一筆。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(new FakeProvider { Response = "x" }).AskAsync("說明", [], "問題", null, cts.Token));

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageOutcome.Cancelled, row.Outcome);
        Assert.Equal(AiUsageFeature.HelpAssistant, row.Feature);
    }

    [Fact]
    public void IsAvailable_WithoutDefaultProvider_ShouldBeFalseWithReason()
    {
        var service = CreateService(new FakeProvider(), defaultProvider: string.Empty);

        Assert.False(service.IsAvailable);
        Assert.Contains("DefaultProvider", service.UnavailableReason);
    }

    [Fact]
    public void Feature_ShouldHaveDisplayNameAndBeTokenBased()
    {
        Assert.Equal("使用說明 AI 助理", AiUsageFeatureText.Describe(AiUsageFeature.HelpAssistant));
        Assert.True(AiUsageFeatureText.IsTokenBased(AiUsageFeature.HelpAssistant));
    }

    private HelpAssistantService CreateService(FakeProvider provider, string defaultProvider = "AzureOpenAI")
    {
        var settings = Options.Create(new LlmSettings { DefaultProvider = defaultProvider });
        var currentUser = new CurrentUserService();
        currentUser.CurrentUser.Id = 7;
        currentUser.CurrentUser.Account = "user";
        currentUser.CurrentUser.Name = "王小明";

        return new HelpAssistantService(
            [provider],
            settings,
            TestAiServices.CreateGateway(context, settings, callLogRecorder, quota),
            currentUser,
            NullLogger<HelpAssistantService>.Instance);
    }

    private sealed class FakeProvider : ITextGenerationProvider
    {
        public string Response { get; init; } = string.Empty;

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

            var midpoint = Response.Length / 2;
            onDelta?.Invoke(Response[..midpoint]);
            onDelta?.Invoke(Response[midpoint..]);

            return Task.FromResult(new TextGenerationResult(Response, new AiTokenUsage(100, 50)));
        }
    }
}
