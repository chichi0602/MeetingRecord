using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 轉錄工作（1.0.29 起經過 AiCallGateway）：會議自己記時長、每段一筆 AI 對話紀錄、開始時檢查用量上限。
/// 轉檔與供應商都用假的——不跑 FFmpeg、不打真實 API。
/// </summary>
public sealed class TranscriptionJobRunnerTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingAiCallLogRecorder callLog = new();
    private readonly FakeAiQuotaService quota = new();
    private readonly FakeTranscriptionProvider provider = new();
    private readonly IOptions<LlmSettings> llmSettings;

    public TranscriptionJobRunnerTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        Directory.CreateDirectory(rootPath);

        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI", TranscriptionProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://contoso.openai.azure.com/",
            ApiKey = "key",
            Model = "gpt-4o-mini",
            TranscriptionModel = "gpt-transcribe",
        };
        llmSettings = Options.Create(settings);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ShouldWriteMeetingDurationAndOneCallLogPerSegment()
    {
        var meetingId = await AddMeetingAsync();

        await CreateRunner().RunAsync(meetingId, CancellationToken.None, requestedByUserId: 7, requestedByUserName: "王小明");

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        Assert.Equal(1500d, meeting.MediaDurationSeconds);   // 15 分＋10 分

        var ledger = await context.AiUsageLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([900d, 600d], ledger.Select(x => x.AudioSeconds!.Value));
        Assert.All(ledger, row => Assert.Equal(7, row.UserId));

        Assert.Equal(2, callLog.Entries.Count);
        Assert.Single(callLog.Entries.Select(x => x.ThreadKey).Distinct());
        Assert.Equal(ledger.Select(x => x.CallId), callLog.Entries.Select(x => (Guid?)x.CallId));
        Assert.EndsWith("語音轉錄 2/2", callLog.Entries[1].RelatedInfo);
        Assert.Equal("第 2 段", callLog.Entries[1].Response.Text);
    }

    [Fact]
    public async Task Run_QuotaReached_ShouldFailBeforeConvertingOrCalling()
    {
        quota.Blocked = FakeAiQuotaService.GlobalReached();
        var meetingId = await AddMeetingAsync();

        await CreateRunner().RunAsync(meetingId, CancellationToken.None, requestedByUserId: 7);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Failed, meeting.TranscriptionStatus);
        Assert.Contains("已達 AI 用量上限", meeting.TranscriptionError);
        Assert.Equal(0, provider.CallCount);
        Assert.False(await context.AiUsageLog.AnyAsync());
        Assert.Equal("Ai.QuotaBlocked", (await context.AuditLog.AsNoTracking().SingleAsync()).Action);
    }

    [Fact]
    public async Task Run_AdmittedJob_ShouldNotCheckQuotaPerSegment()
    {
        // 開始時檢查一次；一旦開始就做完（中途擋下會白花前面幾段的錢又沒有逐字稿）。
        var meetingId = await AddMeetingAsync();

        await CreateRunner().RunAsync(meetingId, CancellationToken.None);

        Assert.Equal(1, quota.CheckCount);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Run_ShouldSaveTranscriptInTraditionalChinese()
    {
        // 1.0.41：轉錄服務輸出簡體，存檔前就要轉成繁體（直接讀檔案本身，不經會再轉一次的 ReadTranscriptAsync）。
        provider.Text = "他说现在没有办法";
        var meetingId = await AddMeetingAsync();

        await CreateRunner().RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        var saved = await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));
        Assert.Contains("他說現在沒有辦法", saved);
        Assert.DoesNotContain("说", saved);
    }

    private TranscriptionJobRunner CreateRunner()
    {
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.MeetingMediaPath = Path.Combine(rootPath, "media");
        systemSettings.ExternalFileSystem.MeetingTranscriptPath = Path.Combine(rootPath, "transcript");

        return new TranscriptionJobRunner(
            context,
            new FakeMediaConverter(rootPath),
            [provider],
            new MeetingFileStore(Options.Create(systemSettings), NullLogger<MeetingFileStore>.Instance),
            llmSettings,
            new TranscriptionProgressNotifier(),
            TestAiServices.CreateGateway(context, llmSettings, callLog, quota),
            NullLogger<TranscriptionJobRunner>.Instance);
    }

    private async Task<int> AddMeetingAsync()
    {
        var meeting = new Meeting
        {
            Title = "週會",
            MediaRelativePath = "2026/10/a.mp4",
            TranscriptionStatus = TranscriptionStatus.Pending,
            MediaDurationSeconds = null,
        };
        context.Meeting.Add(meeting);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return meeting.Id;
    }

    /// <summary>產生兩個假的 mp3 分段：25 分鐘的音檔切成 15 分＋10 分。</summary>
    private sealed class FakeMediaConverter(string rootPath) : IMediaConverter
    {
        public Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken)
        {
            var workingDirectory = Path.Combine(rootPath, "work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            var segments = new[] { "part-000.mp3", "part-001.mp3" }
                .Select(name =>
                {
                    var path = Path.Combine(workingDirectory, name);
                    File.WriteAllBytes(path, [1, 2, 3]);
                    return path;
                })
                .ToList();

            return Task.FromResult(new MediaSegmentSet(workingDirectory, segments, TimeSpan.FromSeconds(1500)));
        }
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-transcribe";

        /// <summary>指定時每段都回這段文字；沒指定時回「第 n 段」。</summary>
        public string? Text { get; set; }

        public Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Text ?? $"第 {CallCount} 段");
        }
    }
}
