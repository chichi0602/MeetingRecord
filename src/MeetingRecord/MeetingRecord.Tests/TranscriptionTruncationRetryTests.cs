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
/// 截斷保險（1.1.61）：轉錄結果長到接近輸出上限時，把該段對半切再重轉。
/// 轉檔、切半、供應商都用假的——不跑 FFmpeg、不打真實 API。
/// </summary>
public sealed class TranscriptionTruncationRetryTests : IAsyncDisposable
{
    private static readonly string LongText = new('長', 1900);

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingAiCallLogRecorder callLog = new();
    private readonly FakeAiQuotaService quota = new();
    private readonly ScriptedProvider provider = new();
    private readonly FakeSplitter splitter = new();
    private readonly IOptions<LlmSettings> llmSettings;

    public TranscriptionTruncationRetryTests()
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
    public async Task Run_TruncatedSegment_ShouldSplitAndReplaceWithBothHalves()
    {
        // 第 1 段回傳頂到上限的長文，兩半各回短文；第 2 段正常。
        provider.Script = new Queue<string>([LongText, "第一段前半", "第一段後半", "第二段"]);
        var meetingId = await AddMeetingAsync();

        await CreateRunner(splitter).RunAsync(meetingId, CancellationToken.None, requestedByUserId: 7);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        var saved = await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));
        Assert.Equal("第一段前半\n第一段後半\n\n第二段", saved);

        // 會議時長只算原始音檔，不因重轉而變長。
        Assert.Equal(480d, meeting.MediaDurationSeconds);

        // 帳本照實記：原本那次（已上傳、已收費）＋兩半＋第 2 段。
        var ledger = await context.AiUsageLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([300d, 120d, 180d, 180d], ledger.Select(x => x.AudioSeconds!.Value));
        Assert.EndsWith("語音轉錄 1/2（前半）", callLog.Entries[1].RelatedInfo);
        Assert.EndsWith("語音轉錄 1/2（後半）", callLog.Entries[2].RelatedInfo);
    }

    [Fact]
    public async Task Run_NormalSegments_ShouldNeverSplit()
    {
        provider.Script = new Queue<string>([new string('短', 1500), "第二段"]);
        var meetingId = await AddMeetingAsync();

        await CreateRunner(splitter).RunAsync(meetingId, CancellationToken.None);

        Assert.Equal(0, splitter.CallCount);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Run_AlwaysLong_ShouldStopAfterTwoLevels()
    {
        // 永遠回長文：每段 1 次 ＋ 兩半 2 次 ＋ 四份 4 次 ＝ 7 次、切 3 次，不會無限切下去。兩段共 14 次、切 6 次。
        provider.Always = LongText;
        var meetingId = await AddMeetingAsync();

        await CreateRunner(splitter).RunAsync(meetingId, CancellationToken.None);

        Assert.Equal(14, provider.CallCount);
        Assert.Equal(6, splitter.CallCount);
        Assert.Equal(TranscriptionStatus.Completed, (await context.Meeting.AsNoTracking().SingleAsync()).TranscriptionStatus);
    }

    [Fact]
    public async Task Run_WithoutSplitter_ShouldKeepOriginalText()
    {
        // 沒有註冊切半服務（例如端對端測試）時照常轉錄，只是少了這道保險。
        provider.Script = new Queue<string>([LongText, "第二段"]);
        var meetingId = await AddMeetingAsync();

        await CreateRunner(segmentSplitter: null).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        var saved = await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));
        Assert.StartsWith(LongText, saved);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Run_SplitFails_ShouldKeepOriginalText()
    {
        splitter.Fail = true;
        provider.Script = new Queue<string>([LongText, "第二段"]);
        var meetingId = await AddMeetingAsync();

        await CreateRunner(splitter).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        Assert.Equal(2, provider.CallCount);
    }

    private TranscriptionJobRunner CreateRunner(IMediaSegmentSplitter? segmentSplitter)
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
            NullLogger<TranscriptionJobRunner>.Instance,
            segmentSplitter);
    }

    private async Task<int> AddMeetingAsync()
    {
        var meeting = new Meeting
        {
            Title = "週會",
            MediaRelativePath = "2026/11/a.mp4",
            TranscriptionStatus = TranscriptionStatus.Pending,
        };
        context.Meeting.Add(meeting);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return meeting.Id;
    }

    /// <summary>兩段：5 分＋3 分，各段秒數由轉檔器算好（切在停頓處時各段不等長）。</summary>
    private sealed class FakeMediaConverter(string rootPath) : IMediaConverter
    {
        public Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken)
        {
            var workingDirectory = Path.Combine(rootPath, "work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            var segments = new[] { "part_0000.mp3", "part_0001.mp3" }
                .Select(name =>
                {
                    var path = Path.Combine(workingDirectory, name);
                    File.WriteAllBytes(path, [1, 2, 3]);
                    return path;
                })
                .ToList();

            return Task.FromResult(new MediaSegmentSet(workingDirectory, segments, TimeSpan.FromSeconds(480), segmentSeconds: [300d, 180d]));
        }
    }

    /// <summary>在原分段旁邊產生兩個檔，秒數 40%／60%。</summary>
    private sealed class FakeSplitter : IMediaSegmentSplitter
    {
        public int CallCount { get; private set; }

        public bool Fail { get; set; }

        public Task<IReadOnlyList<(string FullPath, double Seconds)>?> SplitInHalfAsync(string segmentFullPath, CancellationToken cancellationToken)
        {
            CallCount++;
            if (Fail)
            {
                return Task.FromResult<IReadOnlyList<(string, double)>?>(null);
            }

            var stem = Path.Combine(Path.GetDirectoryName(segmentFullPath)!, Path.GetFileNameWithoutExtension(segmentFullPath));
            var first = $"{stem}_half_0.mp3";
            var second = $"{stem}_half_1.mp3";
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);

            // 原分段的秒數由檔名推不出來，這裡只需要可辨識的數字：第 1 段 300 秒 → 120＋180。
            return Task.FromResult<IReadOnlyList<(string, double)>?>([(first, 120d), (second, 180d)]);
        }
    }

    private sealed class ScriptedProvider : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-transcribe";

        /// <summary>依序回傳；用完之後回空字串。</summary>
        public Queue<string> Script { get; set; } = new();

        /// <summary>有值時每次都回這段（忽略 Script）。</summary>
        public string? Always { get; set; }

        public Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Always ?? (Script.Count > 0 ? Script.Dequeue() : string.Empty));
        }
    }
}
