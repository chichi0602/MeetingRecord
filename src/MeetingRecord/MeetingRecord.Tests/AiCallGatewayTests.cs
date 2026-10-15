using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 付費 AI 呼叫的唯一出入口（1.0.29）：用量上限 → CallId → 呼叫 → 帳本＋對話紀錄（同一個 CallId）。
/// </summary>
public sealed class AiCallGatewayTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly IOptions<LlmSettings> llmSettings = Options.Create(new LlmSettings());
    private readonly RecordingAiCallLogRecorder callLog = new();
    private readonly FakeAiQuotaService quota = new();

    public AiCallGatewayTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    private AiCallGateway CreateGateway() => TestAiServices.CreateGateway(context, llmSettings, callLog, quota);

    private static AiCallContext Context(bool admitted = false) => new(
        AiUsageFeature.AiChat, 7, "王小明", MeetingId: 12, ProjectId: 3,
        RelatedInfo: "會議 #12 AI 問答：進度？", ThreadKey: "chat:Meeting:12:c1", QuotaAdmitted: admitted);

    [Fact]
    public async Task Generate_Success_ShouldWriteLedgerAndCallLogWithTheSameCallId()
    {
        var provider = new FakeTextProvider { Response = "答案在這裡" };

        var result = await CreateGateway().GenerateAsync(provider, Context(), "系統", "問題", null, CancellationToken.None);

        Assert.Equal("答案在這裡", result.Content);
        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        var entry = Assert.Single(callLog.Entries);
        Assert.NotNull(row.CallId);
        Assert.Equal(row.CallId, entry.CallId);

        Assert.Equal(AiUsageOutcome.Succeeded, row.Outcome);
        Assert.Equal(AiUsageFeature.AiChat, entry.Feature);
        Assert.Equal("AI 問答", entry.FeatureText);
        Assert.Equal(7, entry.UserId);
        Assert.Equal(12, entry.MeetingId);
        Assert.Equal("chat:Meeting:12:c1", entry.ThreadKey);
        Assert.Equal(["system", "user"], entry.Request.Messages.Select(x => x.Role));
        Assert.Equal("問題", entry.Request.Messages[1].Content);
        Assert.Equal("答案在這裡", entry.Response.Text);
        Assert.False(entry.Response.IsPartial);
        Assert.Equal(100, entry.Response.InputTokens);
    }

    [Fact]
    public async Task Generate_ShouldStillForwardDeltasToTheCaller()
    {
        var provider = new FakeTextProvider { Response = "一二三四" };
        var deltas = new List<string>();

        await CreateGateway().GenerateAsync(provider, Context(), "s", "u", deltas.Add, CancellationToken.None);

        Assert.Equal("一二三四", string.Concat(deltas));
    }

    [Fact]
    public async Task Generate_HttpFailure_ShouldKeepPartialResponseAndStatus_ThenRethrow()
    {
        var provider = new FakeTextProvider
        {
            Response = "已收到一半",
            Failure = new AiProviderHttpException(429, "文字生成 API 回應 429 Too Many Requests：slow down"),
        };

        var ex = await Assert.ThrowsAsync<AiProviderHttpException>(
            () => CreateGateway().GenerateAsync(provider, Context(), "s", "u", null, CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageOutcome.Failed, row.Outcome);
        var entry = Assert.Single(callLog.Entries);
        Assert.Equal(row.CallId, entry.CallId);
        Assert.Equal(AiUsageOutcome.Failed, entry.Outcome);
        Assert.Equal(429, entry.HttpStatus);
        Assert.Equal("已收到一半", entry.Response.Text);
        Assert.True(entry.Response.IsPartial);
        Assert.Equal(nameof(AiProviderHttpException), entry.ExceptionType);
    }

    [Fact]
    public async Task Generate_Cancelled_ShouldRecordCancelledInBoth()
    {
        var provider = new FakeTextProvider { Failure = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateGateway().GenerateAsync(provider, Context(), "s", "u", null, CancellationToken.None));

        Assert.Equal(AiUsageOutcome.Cancelled, (await context.AiUsageLog.AsNoTracking().SingleAsync()).Outcome);
        Assert.Equal(AiUsageOutcome.Cancelled, Assert.Single(callLog.Entries).Outcome);
    }

    [Fact]
    public async Task Generate_QuotaReached_ShouldNotCallProviderOrRecord_ButAudit()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();
        var provider = new FakeTextProvider { Response = "x" };

        var ex = await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => CreateGateway().GenerateAsync(provider, Context(), "s", "u", null, CancellationToken.None));

        Assert.Contains("已達 AI 用量上限", ex.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.False(await context.AiUsageLog.AnyAsync());
        Assert.Empty(callLog.Entries);
        var audit = await context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Ai.QuotaBlocked", audit.Action);
        Assert.False(audit.Success);
    }

    [Fact]
    public async Task Generate_QuotaAdmitted_ShouldSkipTheCheck()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();
        var provider = new FakeTextProvider { Response = "x" };

        await CreateGateway().GenerateAsync(provider, Context(admitted: true), "s", "u", null, CancellationToken.None);

        Assert.Equal(0, quota.CheckCount);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Admit_QuotaReached_ShouldReturnStatusAndAudit_WithoutThrowing()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();

        var blocked = await CreateGateway().AdmitAsync(Context(), CancellationToken.None);

        Assert.NotNull(blocked);
        Assert.Equal("Ai.QuotaBlocked", (await context.AuditLog.AsNoTracking().SingleAsync()).Action);
    }

    [Fact]
    public async Task Generate_CallLogDisabled_ShouldStillGiveTheLedgerACallId()
    {
        callLog.IsEnabled = false;

        await CreateGateway().GenerateAsync(new FakeTextProvider { Response = "x" }, Context(), "s", "u", null, CancellationToken.None);

        Assert.NotNull((await context.AiUsageLog.AsNoTracking().SingleAsync()).CallId);
        Assert.Empty(callLog.Entries);
    }

    [Fact]
    public async Task Generate_CallLogRecorderThrows_ShouldNotBreakTheCall()
    {
        // 真正的記錄器不會拋（自己吞掉）；這裡確認萬一拋了，呼叫照樣回傳——用真的 AiCallLogService 搭配寫不進去的目錄。
        var service = TestAiServices.CreateCallLogService(context, aiCallLogPath: "Z:\\no-such-drive\\x");
        var gateway = TestAiServices.CreateGateway(context, llmSettings, service, quota);

        var result = await gateway.GenerateAsync(new FakeTextProvider { Response = "ok" }, Context(), "s", "u", null, CancellationToken.None);

        Assert.Equal("ok", result.Content);
        var row = await context.AiCallLog.AsNoTracking().SingleAsync();
        Assert.Null(row.ContentFile);
    }

    [Fact]
    public async Task Generate_Images_ShouldOnlyRecordTypeAndSize()
    {
        var images = new[] { new PromptImage("image/png", [1, 2, 3, 4, 5]) };

        await CreateGateway().GenerateAsync(new FakeTextProvider { Response = "x" }, Context(), "s", "u", null, CancellationToken.None, images);

        var entry = Assert.Single(callLog.Entries);
        var image = Assert.Single(entry.Request.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(5, image.Bytes);
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("AQIDBAU", json); // [1,2,3,4,5] 的 base64
    }

    [Fact]
    public async Task Transcribe_ShouldRecordMetadataAndTextButNeverAudio()
    {
        var provider = new FakeTranscriptionProvider { Text = "大家好，今天開會。" };
        await using var audio = new MemoryStream([9, 9, 9]);

        var text = await CreateGateway().TranscribeAsync(
            provider, Context() with { Feature = AiUsageFeature.Transcription }, audio, "part-001.mp3",
            audioSeconds: 900, isDurationEstimated: false, segmentIndex: 1, segmentCount: 3, CancellationToken.None);

        Assert.Equal("大家好，今天開會。", text);
        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(900, row.AudioSeconds);
        var entry = Assert.Single(callLog.Entries);
        Assert.Equal(AiCallLogRequest.TranscriptionKind, entry.Request.Kind);
        Assert.Equal("part-001.mp3", entry.Request.FileName);
        Assert.Equal(1, entry.Request.SegmentIndex);
        Assert.Equal(3, entry.Request.SegmentCount);
        Assert.Empty(entry.Request.Messages);
        Assert.Equal("大家好，今天開會。", entry.Response.Text);
    }

    [Fact]
    public async Task Transcribe_Failure_ShouldLeaveLedgerSecondsEmpty()
    {
        // 與 1.0.29 之前相同：失敗時沒有轉出結果，不確定供應商算了多少，帳本不填秒數。
        var provider = new FakeTranscriptionProvider { Failure = new InvalidOperationException("boom") };
        await using var audio = new MemoryStream([1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateGateway().TranscribeAsync(
            provider, Context() with { Feature = AiUsageFeature.Transcription }, audio, "a.mp3",
            900, false, 1, 1, CancellationToken.None));

        var row = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AiUsageOutcome.Failed, row.Outcome);
        Assert.Null(row.AudioSeconds);
        Assert.Equal(900, Assert.Single(callLog.Entries).Request.AudioSeconds);
    }

    internal sealed class FakeTextProvider : ITextGenerationProvider
    {
        public string Response { get; init; } = string.Empty;

        public Exception? Failure { get; init; }

        public int CallCount { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-4o-mini";

        public Task<TextGenerationResult> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            Action<string>? onDelta,
            CancellationToken cancellationToken,
            IReadOnlyList<PromptImage>? images = null)
        {
            CallCount++;
            var midpoint = Response.Length / 2;
            onDelta?.Invoke(Response[..midpoint]);
            onDelta?.Invoke(Response[midpoint..]);

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new TextGenerationResult(Response, new AiTokenUsage(100, 50)));
        }
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public string Text { get; init; } = string.Empty;

        public Exception? Failure { get; init; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-transcribe";

        public Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
            => Failure is null ? Task.FromResult(Text) : Task.FromException<string>(Failure);
    }
}
