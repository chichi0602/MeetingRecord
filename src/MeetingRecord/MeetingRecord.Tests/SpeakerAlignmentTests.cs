using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.64 講者整場對齊與標示真名：對齊狀態、代號與真名的套用、聲音參考的請求欄位、轉錄流程。
/// 轉檔、剪檔、供應商都用假的——不跑 FFmpeg、不打真實 API。
/// </summary>
public sealed class SpeakerAlignmentTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingAiCallLogRecorder callLog = new();
    private readonly FakeAiQuotaService quota = new();
    private readonly ScriptedSpeakerProvider provider = new();
    private readonly IOptions<LlmSettings> llmSettings;

    public SpeakerAlignmentTests()
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
            TranscriptionModel = "gpt-4o-transcribe-diarize",
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

    #region SpeakerAlignmentSession

    [Fact]
    public void PlanNewReferences_ShouldPickLongestTurn_InOrderOfFirstAppearance()
    {
        var session = new SpeakerAlignmentSession();
        var plan = session.PlanNewReferences(
        [
            Segment("B", 0, 3),
            Segment("A", 3, 6),
            Segment("B", 6, 20),
            Segment("A", 20, 21),
        ]);

        // B 先開口；最長的一句 14 秒，只取 10 秒。A 最長 3 秒。
        Assert.Equal([("B", 6d, 10d), ("A", 3d, 3d)], plan);
    }

    [Fact]
    public void PlanNewReferences_ShouldSkipShortTurns_KnownAndUnknownSpeakers()
    {
        var session = new SpeakerAlignmentSession();
        Assert.Equal("1", session.Register("data:1"));

        var plan = session.PlanNewReferences(
        [
            Segment("1", 0, 10),
            Segment("A", 10, 11.5),
            Segment(TranscriptSpeakerLabels.UnknownSpeaker, 12, 20),
            Segment("B", 20, 25),
        ]);

        Assert.Equal([("B", 20d, 5d)], plan);
    }

    [Fact]
    public void Register_ShouldStopAtFour_AndPlanNothingAfterwards()
    {
        var session = new SpeakerAlignmentSession();
        Assert.Equal(["1", "2", "3", "4"], Enumerable.Range(0, 4).Select(i => session.Register($"data:{i}")));
        Assert.Throws<InvalidOperationException>(() => session.Register("data:5"));
        Assert.Empty(session.PlanNewReferences([Segment("A", 0, 5)]));
    }

    [Fact]
    public void Relabel_ShouldOnlyRenameRegisteredLetters()
    {
        var renamed = SpeakerAlignmentSession.Relabel(
            [Segment("A", 0, 1), Segment("B", 1, 2), Segment("A", 2, 3)],
            new Dictionary<string, string> { ["A"] = "3" });

        Assert.Equal(["3", "B", "3"], renamed.Select(x => x.Speaker));
    }

    #endregion

    #region TranscriptSpeakerLabels

    [Fact]
    public void ApplyNames_ShouldReplaceOnlyNamedNumbers()
    {
        var text = "講者 1：大家好\r\n講者 2：好\n講者 A：嗯\n\n講者 1：散會";
        var names = new Dictionary<string, string> { ["1"] = "王小明", ["2"] = " " };

        Assert.Equal("王小明：大家好\r\n講者 2：好\n講者 A：嗯\n\n王小明：散會", TranscriptSpeakerLabels.ApplyNames(text, names));
        Assert.Equal(text, TranscriptSpeakerLabels.ApplyNames(text, null));
        Assert.Equal(string.Empty, TranscriptSpeakerLabels.ApplyNames(null, names));
    }

    [Fact]
    public void ListAlignedSpeakers_ShouldSortNumerically_AndIgnoreLetters()
    {
        Assert.Equal(["1", "2", "3"], TranscriptSpeakerLabels.ListAlignedSpeakers("講者 3：c\n講者 A：x\n講者 1：a\n講者 2：b\n講者 1：d"));
        Assert.Empty(TranscriptSpeakerLabels.ListAlignedSpeakers("講者 A：舊的逐字稿"));
    }

    [Fact]
    public void SampleLines_ShouldTakeLongestLinesInOrder_AndTruncate()
    {
        var text = "講者 1：短\n講者 2：別人\n講者 1：比較長的一句\n講者 1：中等的句\n講者 1：" + new string('長', 70);

        var samples = TranscriptSpeakerLabels.SampleLines(text, "1");

        Assert.Equal(["比較長的一句", "中等的句", new string('長', 60) + "…"], samples);
    }

    [Fact]
    public void HasSegmentLabels_ShouldIgnoreNumbers()
    {
        Assert.False(TranscriptSpeakerLabels.HasSegmentLabels("講者 1：a\n講者 2：b"));
        Assert.True(TranscriptSpeakerLabels.HasSegmentLabels("講者 1：a\n講者 A：b"));
        Assert.True(TranscriptSpeakerLabels.HasSegmentLabels("講者 未知：b"));
    }

    [Fact]
    public void Names_ShouldRoundTrip_AndRejectBadInput()
    {
        var json = TranscriptSpeakerLabels.SerializeNames(new Dictionary<string, string> { ["1"] = "王小明" });
        Assert.Equal("王小明", TranscriptSpeakerLabels.ParseNames(json)["1"]);
        Assert.Null(TranscriptSpeakerLabels.SerializeNames(new Dictionary<string, string>()));
        Assert.Empty(TranscriptSpeakerLabels.ParseNames("不是 JSON"));

        Assert.True(TranscriptSpeakerLabels.IsValidName("王小明"));
        Assert.False(TranscriptSpeakerLabels.IsValidName("王：小明"));
        Assert.False(TranscriptSpeakerLabels.IsValidName("王\n小明"));
        Assert.False(TranscriptSpeakerLabels.IsValidName(new string('名', TranscriptSpeakerLabels.MaxNameLength + 1)));
    }

    #endregion

    #region 供應商與剪檔

    [Fact]
    public void Parse_ShouldKeepSegmentTimes()
    {
        var parsed = DiarizedTranscriptFormatter.Parse("""{"text":"你好","segments":[{"speaker":"A","text":"你好","start":1.5,"end":4}]}""");

        var segment = Assert.Single(parsed.Segments);
        Assert.Equal(("A", 1.5, 4d), (segment.Speaker, segment.Start, segment.End));
        Assert.Equal(2.5, segment.Seconds);
    }

    [Fact]
    public async Task TranscribeWithSpeakers_ShouldSendReferences()
    {
        var factory = new CapturingFactory("""{"segments":[{"speaker":"1","text":"你好","start":0,"end":2}]}""");
        var azure = new AzureOpenAiTranscriptionProvider(factory, llmSettings, NullLogger<AzureOpenAiTranscriptionProvider>.Instance);

        using var audio = new MemoryStream([1, 2, 3]);
        var result = await azure.TranscribeWithSpeakersAsync(
            audio, "part_0001.mp3", [new SpeakerReference("1", "data:audio/wav;base64,AAA=")], CancellationToken.None);

        Assert.True(azure.SupportsSpeakers);
        Assert.Equal("講者 1：你好", result.ToText());
        Assert.Equal("1", factory.Fields["known_speaker_names[]"]);
        Assert.Equal("data:audio/wav;base64,AAA=", factory.Fields["known_speaker_references[]"]);
        Assert.Equal("auto", factory.Fields["chunking_strategy"]);
    }

    [Fact]
    public void BuildClipArguments_ShouldCutMonoWav()
    {
        var arguments = FfmpegMediaConverter.BuildClipArguments(@"C:\work\part_0000.mp3", @"C:\work\ref.wav", 12.25, 8);

        Assert.Contains("-ss 12.25 -t 8 -i \"C:\\work\\part_0000.mp3\"", arguments);
        Assert.Contains("-ac 1 -ar 16000 -c:a pcm_s16le \"C:\\work\\ref.wav\"", arguments);
    }

    #endregion

    #region TranscriptionJobRunner

    [Fact]
    public async Task Run_ShouldKeepSpeakerNumbersAcrossSegments_AndClearOldNames()
    {
        // 第 1 段：A、B 都是新講者 → 剪參考、改成 1、2。第 2 段帶兩份參考，認得的回 1、2；新出現的 A → 3。
        provider.Script.Enqueue(Result(Segment("A", 0, 5, "大家好"), Segment("B", 5, 9, "開始吧"), Segment("A", 9, 10, "好")));
        provider.Script.Enqueue(Result(Segment("1", 0, 4, "第二段"), Segment("2", 4, 8, "收到"), Segment("A", 8, 12, "我是新來的")));
        var meetingId = await AddMeetingAsync(speakerNames: """{"1":"舊名字"}""");

        await CreateRunner(new FakeClipCutter()).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        Assert.Null(meeting.SpeakerNames);
        Assert.Equal(
            "講者 1：大家好\n講者 2：開始吧\n講者 1：好\n\n講者 1：第二段\n講者 2：收到\n講者 3：我是新來的",
            await ReadSavedTranscriptAsync(meeting));
        Assert.Equal([0, 2], provider.ReferenceCounts);
        Assert.Equal(0, provider.PlainCalls);
    }

    [Fact]
    public async Task Run_ReferencesRejected_ShouldFallBackToSegmentLetters()
    {
        provider.Script.Enqueue(Result(Segment("A", 0, 5, "大家好")));
        provider.RejectWhenReferenced = true;
        provider.PlainText = "講者 A：退回後的第二段";
        var meetingId = await AddMeetingAsync();

        await CreateRunner(new FakeClipCutter()).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        Assert.Equal("講者 1：大家好\n\n講者 A：退回後的第二段", await ReadSavedTranscriptAsync(meeting));
        Assert.Equal(1, provider.PlainCalls);
    }

    [Fact]
    public async Task Run_WithoutClipCutter_ShouldUseSegmentLetters()
    {
        provider.PlainText = "講者 A：照舊";
        var meetingId = await AddMeetingAsync();

        await CreateRunner(clipCutter: null).RunAsync(meetingId, CancellationToken.None);

        Assert.Empty(provider.ReferenceCounts);
        Assert.Equal(2, provider.PlainCalls);
    }

    [Fact]
    public async Task Run_ShouldSaveSpeakerClips_AndReplaceOldOnesOnRetranscribe()
    {
        // 1.1.71：聲音參考存下來給「標示講者」試聽；只存最終逐字稿有出現的講者，重新轉錄時換新、舊檔刪掉。
        provider.Script.Enqueue(Result(Segment("A", 0, 5, "大家好"), Segment("B", 5, 9, "開始吧")));
        provider.Script.Enqueue(Result(Segment("1", 0, 4, "第二段")));
        var meetingId = await AddMeetingAsync();

        await CreateRunner(new FakeClipCutter()).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync();
        var clips = TranscriptSpeakerLabels.ParseNames(meeting.SpeakerClips);
        Assert.Equal(["1", "2"], clips.Keys.Order());
        var firstClip = Path.Combine(rootPath, "transcript", clips["1"]);
        Assert.Equal([9, 9], await File.ReadAllBytesAsync(firstClip));

        // 重新轉錄：這次只有一位講者。
        await context.Meeting.Where(x => x.Id == meetingId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TranscriptionStatus, TranscriptionStatus.Pending));
        provider.Script.Enqueue(Result(Segment("A", 0, 5, "重來")));
        provider.Script.Enqueue(Result(Segment("1", 0, 4, "第二段")));

        await CreateRunner(new FakeClipCutter()).RunAsync(meetingId, CancellationToken.None);

        var retranscribed = TranscriptSpeakerLabels.ParseNames((await context.Meeting.AsNoTracking().SingleAsync()).SpeakerClips);
        Assert.Equal(["1"], retranscribed.Keys);
        Assert.NotEqual(clips["1"], retranscribed["1"]);
        Assert.False(File.Exists(firstClip));
        Assert.False(File.Exists(Path.Combine(rootPath, "transcript", clips["2"])));
    }

    [Fact]
    public async Task Run_WithoutClipCutter_ShouldNotSaveSpeakerClips()
    {
        provider.PlainText = "大家好";
        var meetingId = await AddMeetingAsync();

        await CreateRunner(clipCutter: null).RunAsync(meetingId, CancellationToken.None);

        Assert.Null((await context.Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId)).SpeakerClips);
    }

    #endregion

    private static DiarizedSegment Segment(string speaker, double start, double end, string text = "話") =>
        new(speaker, text, start, end);

    private static DiarizedTranscription Result(params DiarizedSegment[] segments) => new(segments, string.Empty);

    private async Task<string> ReadSavedTranscriptAsync(Meeting meeting) =>
        await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));

    private TranscriptionJobRunner CreateRunner(IAudioClipCutter? clipCutter)
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
            segmentSplitter: null,
            clipCutter);
    }

    private async Task<int> AddMeetingAsync(string? speakerNames = null)
    {
        var meeting = new Meeting
        {
            Title = "週會",
            MediaRelativePath = "2026/11/a.mp4",
            TranscriptionStatus = TranscriptionStatus.Pending,
            SpeakerNames = speakerNames,
        };
        context.Meeting.Add(meeting);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return meeting.Id;
    }

    /// <summary>兩段：5 分＋3 分。</summary>
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

    private sealed class FakeClipCutter : IAudioClipCutter
    {
        public Task<string?> CutWavClipAsync(string sourceFullPath, double startSeconds, double seconds, CancellationToken cancellationToken)
        {
            var path = Path.Combine(Path.GetDirectoryName(sourceFullPath)!, $"ref_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, [9, 9]);
            return Task.FromResult<string?>(path);
        }
    }

    private sealed class ScriptedSpeakerProvider : ISpeakerAwareTranscriptionProvider
    {
        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-4o-transcribe-diarize";

        public bool SupportsSpeakers => true;

        public Queue<DiarizedTranscription> Script { get; } = new();

        /// <summary>每次分講者呼叫帶了幾份聲音參考。</summary>
        public List<int> ReferenceCounts { get; } = [];

        /// <summary>帶參考就回 400（模擬 Azure 不支援 known_speaker_*）。</summary>
        public bool RejectWhenReferenced { get; set; }

        public string PlainText { get; set; } = string.Empty;

        public int PlainCalls { get; private set; }

        public Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
        {
            PlainCalls++;
            return Task.FromResult(PlainText);
        }

        public Task<DiarizedTranscription> TranscribeWithSpeakersAsync(
            Stream audio,
            string fileName,
            IReadOnlyList<SpeakerReference> references,
            CancellationToken cancellationToken)
        {
            ReferenceCounts.Add(references.Count);
            if (RejectWhenReferenced && references.Count > 0)
            {
                throw new AiProviderHttpException(400, "known_speaker_references is not supported");
            }

            return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : new DiarizedTranscription([], string.Empty));
        }
    }

    /// <summary>回固定內容，並記下 multipart 裡的文字欄位（同名欄位留最後一個）。</summary>
    private sealed class CapturingFactory(string body) : IHttpClientFactory
    {
        public Dictionary<string, string> Fields { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, body), disposeHandler: true);

        private sealed class Handler(CapturingFactory owner, string body) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Content is MultipartFormDataContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        var fieldName = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (fieldName is not null && part is StringContent)
                        {
                            owner.Fields[fieldName] = await part.ReadAsStringAsync(cancellationToken);
                        }
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
