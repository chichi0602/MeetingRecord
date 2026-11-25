using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.73：影音轉文字稿可選 Azure LLM Speech。請求內容、回應解析、段落與講者標示、選項驗證，
/// 以及轉錄工作走 LLM Speech 的整條路（假的 HTTP、假的轉檔，不打真實 API）。
/// </summary>
public sealed class LlmSpeechTranscriptionTests : IAsyncDisposable
{
    private const string DiarizedBody = """
        {
          "durationMilliseconds": 400000,
          "combinedPhrases": [{ "text": "大家好今天開會好的" }],
          "phrases": [
            { "speaker": 1, "offsetMilliseconds": 0, "durationMilliseconds": 4000, "text": "大家好" },
            { "speaker": 1, "offsetMilliseconds": 4000, "durationMilliseconds": 3000, "text": "今天開會" },
            { "speaker": 2, "offsetMilliseconds": 310000, "durationMilliseconds": 2500, "text": "好的" }
          ]
        }
        """;

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));

    public LlmSpeechTranscriptionTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        Directory.CreateDirectory(rootPath);
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

    #region 請求與回應

    [Fact]
    public void BuildDefinition_Diarize_ShouldEnableEnhancedModeLocaleDiarizationAndPhrases()
    {
        var json = AzureLlmSpeechTranscriptionProvider.BuildDefinition(
            "zh-TW",
            new LlmSpeechOptions(true, 99, [" 王小明 ", "王小明", "", "專案甲"]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("enhancedMode").GetProperty("enabled").GetBoolean());
        Assert.Equal("transcribe", root.GetProperty("enhancedMode").GetProperty("task").GetString());
        Assert.Equal("zh-TW", root.GetProperty("locales")[0].GetString());
        Assert.True(root.GetProperty("diarization").GetProperty("enabled").GetBoolean());
        Assert.Equal(AzureSpeechSettings.MaxSpeakers, root.GetProperty("diarization").GetProperty("maxSpeakers").GetInt32());
        Assert.Equal(["王小明", "專案甲"], root.GetProperty("phraseList").GetProperty("phrases").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void BuildDefinition_NoDiarizeNoPhrases_ShouldOmitThoseKeys()
    {
        using var document = JsonDocument.Parse(AzureLlmSpeechTranscriptionProvider.BuildDefinition("zh-TW", new LlmSpeechOptions(false, 5, [])));

        Assert.False(document.RootElement.TryGetProperty("diarization", out _));
        Assert.False(document.RootElement.TryGetProperty("phraseList", out _));
    }

    [Fact]
    public void Parse_Diarize_ShouldReturnSpeakerNumbersAndSeconds()
    {
        var result = AzureLlmSpeechTranscriptionProvider.Parse(DiarizedBody, diarize: true);

        Assert.Equal(["1", "1", "2"], result.Segments.Select(x => x.Speaker));
        Assert.Equal(4d, result.Segments[1].Start);
        Assert.Equal(7d, result.Segments[1].End);
    }

    [Fact]
    public void Parse_PhraseWithoutSpeaker_ShouldBeUnknown()
    {
        var result = AzureLlmSpeechTranscriptionProvider.Parse(
            """{ "phrases": [{ "offsetMilliseconds": 0, "durationMilliseconds": 1000, "text": "嗯" }] }""",
            diarize: true);

        Assert.Equal(TranscriptSpeakerLabels.UnknownSpeaker, Assert.Single(result.Segments).Speaker);
    }

    [Fact]
    public void Parse_NoDiarize_ShouldReturnPlainParagraphsEveryFiveMinutes()
    {
        var result = AzureLlmSpeechTranscriptionProvider.Parse(DiarizedBody, diarize: false);

        Assert.Empty(result.Segments);
        Assert.Equal("大家好今天開會\n\n好的", result.FallbackText);
    }

    [Fact]
    public void FormatTranscript_Diarize_ShouldLabelSpeakersAndSplitParagraphsEveryFiveMinutes()
    {
        var parsed = AzureLlmSpeechTranscriptionProvider.Parse(DiarizedBody, diarize: true);

        var text = AzureLlmSpeechTranscriptionProvider.FormatTranscript(parsed.Segments, parsed.FallbackText, diarize: true);

        // \n\n 是逐字稿的分段邊界（產生會議紀錄靠它切），整場一次回來也要每 5 分鐘分一段。
        Assert.Equal("講者 1：大家好今天開會\n\n講者 2：好的", text);
    }

    [Fact]
    public void DescribeFailure_Unauthorized_ShouldPointAtApiKey()
        => Assert.Contains("ApiKey", AzureLlmSpeechTranscriptionProvider.DescribeFailure(401, "{}"));

    [Fact]
    public void BuildRequestUri_ShouldUseFastTranscriptionPath()
        => Assert.Equal(
            "https://contoso.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
            AzureLlmSpeechTranscriptionProvider.BuildRequestUri("https://contoso.cognitiveservices.azure.com/", "2025-10-15").ToString());

    #endregion

    #region 講者標示

    [Fact]
    public void LabelLlmSpeechSegments_SinglePart_ShouldKeepNumbers()
    {
        var segments = new List<DiarizedSegment> { new("1", "a", 0, 1) };

        Assert.Same(segments, TranscriptionJobRunner.LabelLlmSpeechSegments(segments, 1, 1));
    }

    [Fact]
    public void LabelLlmSpeechSegments_MultipleParts_ShouldPrefixPartNumberExceptUnknown()
    {
        var segments = new List<DiarizedSegment> { new("1", "a", 0, 1), new(TranscriptSpeakerLabels.UnknownSpeaker, "b", 1, 2) };

        var labelled = TranscriptionJobRunner.LabelLlmSpeechSegments(segments, 2, 3);

        Assert.Equal(["2-1", TranscriptSpeakerLabels.UnknownSpeaker], labelled.Select(x => x.Speaker));
    }

    [Fact]
    public void PlanLlmSpeechClips_ShouldPickLongestUtterancePerSpeakerCappedAtTenSeconds()
    {
        var plan = TranscriptionJobRunner.PlanLlmSpeechClips(
        [
            new("1", "短", 0, 1),          // 不到 2 秒，不算
            new("1", "長", 10, 40),        // 30 秒，取 10 秒
            new("2", "中", 50, 55),
        ]);

        Assert.Equal([("1", 10d, 10d), ("2", 50d, 5d)], plan);
    }

    [Fact]
    public void TranscriptSpeakerLabels_PartLabels_ShouldBeNameableAndSorted()
    {
        const string text = "講者 2-1：你好\n講者 1-2：嗨\n講者 1-1：早";

        Assert.Equal(["1-1", "1-2", "2-1"], TranscriptSpeakerLabels.ListAlignedSpeakers(text));
        Assert.False(TranscriptSpeakerLabels.HasSegmentLabels(text));
        Assert.Equal("王小明：你好\n講者 1-2：嗨\n講者 1-1：早",
            TranscriptSpeakerLabels.ApplyNames(text, new Dictionary<string, string> { ["2-1"] = "王小明" }));
    }

    [Fact]
    public void TranscriptSpeakerLabels_MoreThanFourSpeakers_ShouldAllBeListed()
        => Assert.Equal(["1", "2", "10"], TranscriptSpeakerLabels.ListAlignedSpeakers("講者 10：c\n講者 2：b\n講者 1：a"));

    #endregion

    #region 選項驗證

    [Fact]
    public void Resolve_NullOptions_ShouldKeepConfiguredDeployment()
    {
        var (options, error) = CreateResolver().Resolve(null);

        Assert.Null(error);
        Assert.Equal(new ResolvedTranscriptionOptions(TranscriptionEngine.AzureOpenAI, null, null), options);
    }

    [Fact]
    public void Resolve_LlmSpeechNotConfigured_ShouldExplain()
    {
        var (_, error) = CreateResolver(speechConfigured: false).Resolve(new TranscriptionOptionsDto { Engine = 1, Diarize = true, MaxSpeakers = 4 });

        Assert.Contains("尚未設定 Azure LLM Speech", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(36)]
    public void Resolve_LlmSpeechDiarizeWithBadMaxSpeakers_ShouldReject(int? maxSpeakers)
    {
        var (_, error) = CreateResolver().Resolve(new TranscriptionOptionsDto { Engine = 1, Diarize = true, MaxSpeakers = maxSpeakers });

        Assert.Contains("講者上限", error);
    }

    [Fact]
    public void Resolve_LlmSpeechNoDiarize_ShouldIgnoreMaxSpeakers()
    {
        var (options, error) = CreateResolver().Resolve(new TranscriptionOptionsDto { Engine = 1, Diarize = false, MaxSpeakers = 99 });

        Assert.Null(error);
        Assert.Equal(new ResolvedTranscriptionOptions(TranscriptionEngine.AzureLlmSpeech, false, null), options);
    }

    [Fact]
    public void Resolve_OpenAiNoDiarizeWithoutPlainDeployment_ShouldExplain()
    {
        var (_, error) = CreateResolver(noSpeakersDeployment: "").Resolve(new TranscriptionOptionsDto { Engine = 0, Diarize = false });

        Assert.Contains("TranscriptionModelNoSpeakers", error);
    }

    [Fact]
    public void ResolveOpenAiDeployment_ShouldSwitchBetweenDiarizeAndPlainDeployments()
    {
        var resolver = CreateResolver(noSpeakersDeployment: "gpt-transcribe");

        Assert.Equal("gpt-4o-transcribe-diarize", resolver.ResolveOpenAiDeployment(true));
        Assert.Equal("gpt-transcribe", resolver.ResolveOpenAiDeployment(false));
        Assert.Equal("gpt-4o-transcribe-diarize", resolver.ResolveOpenAiDeployment(null));
        Assert.Equal(AzureSpeechSettings.PricingModel, resolver.PricingModel(TranscriptionEngine.AzureLlmSpeech, true));
        Assert.Equal("gpt-transcribe", resolver.PricingModel(TranscriptionEngine.AzureOpenAI, false));
    }

    [Fact]
    public void GetMeta_DefaultEngineNotConfigured_ShouldFallBackAndClampSpeakers()
    {
        var meta = CreateResolver(speechConfigured: false, defaultEngine: "AzureLlmSpeech").GetMeta(participantCount: 60);

        Assert.Equal((int)TranscriptionEngine.AzureOpenAI, meta.DefaultEngine);
        Assert.False(meta.LlmSpeechAvailable);
        Assert.True(meta.OpenAiDiarizeAvailable);
        Assert.False(meta.OpenAiPlainAvailable);
        Assert.Equal(AzureSpeechSettings.MaxSpeakers, meta.SuggestedMaxSpeakers);
    }

    [Fact]
    public void BuildPhrases_ShouldTakeProjectTitleAndRoster()
        => Assert.Equal(["專案甲", "王小明", "李小華"],
            TranscriptionOptionsResolver.BuildPhrases(" 專案甲 ", MeetingRecord.Business.Helpers.TagStringHelper.ToStored(["王小明", "李小華"])));

    #endregion

    #region 轉錄工作

    [Fact]
    public async Task Run_LlmSpeech_ShouldSendWholeFileWithPhrasesAndSaveNumberedSpeakersAndClips()
    {
        var project = new Project { Title = "專案甲", Participants = MeetingRecord.Business.Helpers.TagStringHelper.ToStored(["王小明"]) };
        context.Project.Add(project);
        await context.SaveChangesAsync();
        var meetingId = await AddMeetingAsync(TranscriptionEngine.AzureLlmSpeech, diarize: true, maxSpeakers: 6, projectId: project.Id);
        var http = new ScriptedHttpFactory(DiarizedBody);
        var converter = new FakeMediaConverter(rootPath, [400d]);

        await CreateRunner(http, converter).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId);
        Assert.Equal(TranscriptionStatus.Completed, meeting.TranscriptionStatus);
        Assert.Equal(7200, converter.LastMaxSegmentSeconds);
        Assert.NotNull(meeting.TranscriptionElapsedSeconds);

        var definition = JsonDocument.Parse(Assert.Single(http.Definitions)).RootElement;
        Assert.Equal(6, definition.GetProperty("diarization").GetProperty("maxSpeakers").GetInt32());
        Assert.Equal(["專案甲", "王小明"], definition.GetProperty("phraseList").GetProperty("phrases").EnumerateArray().Select(x => x.GetString()));

        var transcript = await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));
        Assert.Equal("講者 1：大家好今天開會\n\n講者 2：好的", transcript.TrimStart('﻿'));
        Assert.Equal(["1", "2"], TranscriptSpeakerLabels.ParseNames(meeting.SpeakerClips).Keys.Order());

        var ledger = await context.AiUsageLog.AsNoTracking().SingleAsync();
        Assert.Equal(AzureSpeechSettings.PricingModel, ledger.Model);
    }

    [Fact]
    public async Task Run_LlmSpeechSplitIntoParts_ShouldLabelSpeakersPerPart()
    {
        var meetingId = await AddMeetingAsync(TranscriptionEngine.AzureLlmSpeech, diarize: true, maxSpeakers: 4);

        await CreateRunner(new ScriptedHttpFactory(DiarizedBody), new FakeMediaConverter(rootPath, [7200d, 400d])).RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId);
        var transcript = await File.ReadAllTextAsync(Path.Combine(rootPath, "transcript", meeting.TranscriptRelativePath!));
        Assert.Equal(["1-1", "1-2", "2-1", "2-2"], TranscriptSpeakerLabels.ListAlignedSpeakers(transcript));
    }

    [Fact]
    public async Task Run_LlmSpeechNotConfigured_ShouldFailWithReason()
    {
        var meetingId = await AddMeetingAsync(TranscriptionEngine.AzureLlmSpeech, diarize: true, maxSpeakers: 4);

        await CreateRunner(new ScriptedHttpFactory(DiarizedBody), new FakeMediaConverter(rootPath, [60d]), speechConfigured: false)
            .RunAsync(meetingId, CancellationToken.None);

        var meeting = await context.Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId);
        Assert.Equal(TranscriptionStatus.Failed, meeting.TranscriptionStatus);
        Assert.Contains("Azure LLM Speech", meeting.TranscriptionError);
    }

    #endregion

    private static TranscriptionOptionsResolver CreateResolver(
        bool speechConfigured = true,
        string noSpeakersDeployment = "",
        string defaultEngine = "AzureOpenAI")
        => new(Options.Create(CreateLlmSettings(noSpeakersDeployment)), Options.Create(CreateTranscriptionSettings(speechConfigured, defaultEngine)));

    private static LlmSettings CreateLlmSettings(string noSpeakersDeployment = "")
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI", TranscriptionProvider = "AzureOpenAI" };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://contoso.openai.azure.com/",
            ApiKey = "key",
            Model = "gpt-4o-mini",
            TranscriptionModel = "gpt-4o-transcribe-diarize",
            TranscriptionModelNoSpeakers = noSpeakersDeployment,
        };
        settings.Pricing[AzureSpeechSettings.PricingModel] = new LlmPricingSettings { AudioPerMinute = 0.006m };
        return settings;
    }

    private static TranscriptionSettings CreateTranscriptionSettings(bool speechConfigured = true, string defaultEngine = "AzureOpenAI") => new()
    {
        DefaultEngine = defaultEngine,
        AzureSpeech = speechConfigured
            ? new AzureSpeechSettings { Endpoint = "https://contoso.cognitiveservices.azure.com/", ApiKey = "speech-key" }
            : new AzureSpeechSettings(),
    };

    private TranscriptionJobRunner CreateRunner(ScriptedHttpFactory http, FakeMediaConverter converter, bool speechConfigured = true)
    {
        var systemSettings = new SystemSettings();
        systemSettings.ExternalFileSystem.MeetingMediaPath = Path.Combine(rootPath, "media");
        systemSettings.ExternalFileSystem.MeetingTranscriptPath = Path.Combine(rootPath, "transcript");

        var llmSettings = Options.Create(CreateLlmSettings());
        var transcriptionSettings = Options.Create(CreateTranscriptionSettings(speechConfigured));
        return new TranscriptionJobRunner(
            context,
            converter,
            [],
            new MeetingFileStore(Options.Create(systemSettings), NullLogger<MeetingFileStore>.Instance),
            llmSettings,
            new TranscriptionProgressNotifier(),
            TestAiServices.CreateGateway(context, llmSettings, new RecordingAiCallLogRecorder(), new FakeAiQuotaService()),
            NullLogger<TranscriptionJobRunner>.Instance,
            clipCutter: new FakeClipCutter(),
            optionsResolver: new TranscriptionOptionsResolver(llmSettings, transcriptionSettings),
            llmSpeechProvider: new AzureLlmSpeechTranscriptionProvider(http, transcriptionSettings, NullLogger<AzureLlmSpeechTranscriptionProvider>.Instance),
            transcriptionSettings: transcriptionSettings);
    }

    private async Task<int> AddMeetingAsync(TranscriptionEngine engine, bool? diarize, int? maxSpeakers, int? projectId = null)
    {
        var meeting = new Meeting
        {
            Title = "週會",
            MediaRelativePath = "2026/11/a.mp4",
            TranscriptionStatus = TranscriptionStatus.Pending,
            TranscriptionEngine = engine,
            TranscriptionDiarize = diarize,
            TranscriptionMaxSpeakers = maxSpeakers,
            ProjectId = projectId,
        };
        context.Meeting.Add(meeting);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return meeting.Id;
    }

    /// <summary>依給的各段秒數產生假的 mp3 分段，並記下呼叫端要的每段上限。</summary>
    private sealed class FakeMediaConverter(string rootPath, double[] partSeconds) : IMediaConverter
    {
        public int? LastMaxSegmentSeconds { get; private set; }

        public Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken)
            => ConvertToMp3SegmentsAsync(sourceFullPath, 300, cancellationToken);

        public Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, int maxSegmentSeconds, CancellationToken cancellationToken)
        {
            LastMaxSegmentSeconds = maxSegmentSeconds;
            var workingDirectory = Path.Combine(rootPath, "work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            var segments = partSeconds
                .Select((_, index) =>
                {
                    var path = Path.Combine(workingDirectory, $"part_{index:0000}.mp3");
                    File.WriteAllBytes(path, [1, 2, 3]);
                    return path;
                })
                .ToList();

            return Task.FromResult(new MediaSegmentSet(
                workingDirectory, segments, TimeSpan.FromSeconds(partSeconds.Sum()), segmentSeconds: partSeconds));
        }
    }

    private sealed class FakeClipCutter : IAudioClipCutter
    {
        public Task<string?> CutWavClipAsync(string sourceFullPath, double startSeconds, double seconds, CancellationToken cancellationToken)
        {
            var path = Path.Combine(Path.GetDirectoryName(sourceFullPath)!, $"clip_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, [9, 9]);
            return Task.FromResult<string?>(path);
        }
    }

    /// <summary>每次都回同一份 JSON，並記下送出的 definition。</summary>
    private sealed class ScriptedHttpFactory(string body) : IHttpClientFactory
    {
        public List<string> Definitions { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, body), disposeHandler: true);

        private sealed class Handler(ScriptedHttpFactory owner, string body) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("speech-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
                if (request.Content is MultipartFormDataContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        if (part.Headers.ContentDisposition?.Name?.Trim('"') == "definition")
                        {
                            owner.Definitions.Add(await part.ReadAsStringAsync(cancellationToken));
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
