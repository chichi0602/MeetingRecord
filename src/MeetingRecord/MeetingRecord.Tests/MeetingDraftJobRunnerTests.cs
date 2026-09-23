using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 草稿生成工作流程的測試：狀態轉換、提示詞代入、map-reduce 分段與失敗處理。
/// 用手寫的假供應商取代真實 LLM，不會發出任何網路請求。
/// </summary>
public sealed class MeetingDraftJobRunnerTests
{
    #region 名詞與與會人員名單

    [Fact]
    public async Task RunAsync_ShouldPrependNameGuidance_WhenProjectHasGlossary()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "週會", "內容", template,
            glossaryTerms: "\n甲專案\n乙系統\n",
            attendees: "\n王小明\n");

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var prompt = provider.Calls[0].UserPrompt;

        // 名單要在最前面：範本多半把 {{transcript}} 擺結尾，附加在後面會緊貼逐字稿。
        Assert.StartsWith("【本次會議的專有名詞與人名對照】", prompt);
        Assert.Contains("常用名詞：甲專案、乙系統", prompt);
        Assert.Contains("與會人員：王小明", prompt);
        Assert.Contains("整理：內容", prompt);
        Assert.EndsWith("（再次提醒：人名與專有名詞請依開頭名單的正確寫法。）", prompt);
    }

    [Fact]
    public async Task RunAsync_ShouldUseLatestGlossary_NotASnapshot()
    {
        // 常用名詞刻意不快照在 Meeting 上——名詞表更新後重跑就該生效。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "週會", "內容", template, glossaryTerms: "\n舊名詞\n");

        var project = await fixture.Context.Project.FirstAsync();
        project.GlossaryTerms = "\n新名詞\n";
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        Assert.Contains("新名詞", provider.Calls[0].UserPrompt);
        Assert.DoesNotContain("舊名詞", provider.Calls[0].UserPrompt);
    }

    [Fact]
    public async Task RunAsync_ShouldInjectNameGuidanceIntoEveryChunkCall()
    {
        // ⚠️ 這是最重要的一筆：map 階段用的是硬寫的提示詞（不是使用者範本）。
        // 只注入 reduce 的話，長逐字稿的人名在摘要階段就被壓縮掉了——而長會議正是
        // 這個功能最有價值的場景，也最容易只改一半沒發現。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var longTranscript = string.Join(
            "\n\n",
            Enumerable.Range(0, 6).Select(i => new string((char)('A' + i), 5000)));
        var meeting = await fixture.AddCompletedMeetingAsync(
            "長會議", longTranscript, template, attendees: "\n王小明\n");

        var provider = new FakeTextGenerationProvider("摘要");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        Assert.True(provider.Calls.Count > 1, "逐字稿應該長到需要分段");
        Assert.All(provider.Calls, call => Assert.Contains("與會人員：王小明", call.UserPrompt));
    }

    [Fact]
    public async Task RunAsync_ShouldNotChangePromptAtAll_WhenNoListsConfigured()
    {
        // 沒設名單時提示詞必須與 0.4.70 之前逐字元相同。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        Assert.Equal("整理：內容", provider.Calls[0].UserPrompt);
    }

    #endregion

    #region 單段（不需分段摘要）

    [Fact]
    public async Task RunAsync_ShouldWriteDraftAndSubstituteVariables_WhenTranscriptFitsInOneChunk()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync(
            "會議「{{meetingTitle}}」（{{meetingDate}}）的逐字稿：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync(
            "需求確認會議",
            transcript: "今天討論了三件事。",
            promptTemplate: template,
            meetingDate: new DateTime(2026, 8, 28));

        var provider = new FakeTextGenerationProvider("整理好的會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Completed, saved.DraftStatus);
        Assert.Equal("整理好的會議紀錄", saved.DraftContent);
        Assert.Null(saved.DraftError);
        Assert.NotNull(saved.DraftCompletedAt);

        // 只有一段時直接送原文，不做「摘要後再整理」。
        var call = Assert.Single(provider.Calls);
        Assert.Equal("會議「需求確認會議」（2026/08/28）的逐字稿：今天討論了三件事。", call.UserPrompt);
    }

    [Fact]
    public async Task RunAsync_ShouldInstructTraditionalChineseInSystemPrompt()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        Assert.Contains("繁體中文", provider.Calls[0].SystemPrompt);
    }

    #endregion

    #region 多段（map-reduce）

    [Fact]
    public async Task RunAsync_ShouldSummariseEachChunkThenCombine_WhenTranscriptIsLong()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");

        // 兩個各 10000 字元的轉錄分段，超過 12000 的單次上限，會被切成兩塊。
        var longTranscript = string.Join(
            "\n\n",
            new string('甲', 10000),
            new string('乙', 10000));
        var meeting = await fixture.AddCompletedMeetingAsync("長會議", longTranscript, template);

        var provider = new FakeTextGenerationProvider("段落摘要");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        // 兩次 map（逐段摘要）＋ 一次 reduce（套提示詞）。
        Assert.Equal(3, provider.Calls.Count);
        Assert.Contains("第 1/2 段", provider.Calls[0].UserPrompt);
        Assert.Contains("第 2/2 段", provider.Calls[1].UserPrompt);

        // reduce 收到的是各段摘要，不是原始逐字稿。
        Assert.Contains("【第 1 段】", provider.Calls[2].UserPrompt);
        Assert.Contains("【第 2 段】", provider.Calls[2].UserPrompt);
        Assert.DoesNotContain(new string('甲', 100), provider.Calls[2].UserPrompt);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Completed, saved.DraftStatus);
    }

    #endregion

    #region 用量帳本（0.4.80）

    [Fact]
    public async Task RunAsync_ShouldRecordOneLedgerRowPerApiCall()
    {
        // ⚠️ 「一次生成 = 一次呼叫」是錯的。長逐字稿會走 map-reduce：
        // N 次分段摘要 ＋ 1 次最終整理。只記一筆的話，長會議的成本會被嚴重低估——
        // 而長會議正是最貴的那種。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var longTranscript = string.Join(
            "\n\n",
            new string('甲', 10000),
            new string('乙', 10000));
        var meeting = await fixture.AddCompletedMeetingAsync("長會議", longTranscript, template);

        var provider = new FakeTextGenerationProvider("段落摘要");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var ledger = await fixture.ReadUsageLogAsync();

        // 帳本的筆數必須等於實際的 API 呼叫次數。
        Assert.Equal(provider.Calls.Count, ledger.Count);
        Assert.Equal(2, ledger.Count(x => x.Feature == AiUsageFeature.MeetingDraftChunkSummary));
        Assert.Equal(1, ledger.Count(x => x.Feature == AiUsageFeature.MeetingDraft));
        Assert.All(ledger, x => Assert.Equal(AiUsageOutcome.Succeeded, x.Outcome));
        Assert.All(ledger, x => Assert.Equal(meeting.Id, x.MeetingId));
        Assert.All(ledger, x => Assert.Equal("gpt-4o-mini", x.Model));
    }

    [Fact]
    public async Task RunAsync_ShouldRecordTokensAndCost()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var row = Assert.Single(await fixture.ReadUsageLogAsync());

        Assert.Equal(100, row.InputTokens);
        Assert.Equal(50, row.OutputTokens);

        // 100/1e6*0.15 + 50/1e6*0.60 = 0.000015 + 0.00003 = 0.000045
        Assert.Equal(0.000045m, row.EstimatedCost);

        // 單價一起存下來，日後才回答得出「這個數字是怎麼算的」。
        Assert.Equal(0.15m, row.InputPricePerMillion);
        Assert.Equal(0.60m, row.OutputPricePerMillion);

        // 文字生成不是按時長計費，時長欄位必須留空。
        Assert.Null(row.AudioSeconds);
    }

    [Fact]
    public async Task RunAsync_ShouldStillRecord_WhenChunkSummaryComesBackBlank()
    {
        // ⚠️ 空白摘要會被 continue 跳過，但那一次呼叫**照樣花了錢**。
        // 記帳必須放在 continue 之前——這個專案在「進度回報」上已經踩過同一個坑。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var longTranscript = string.Join(
            "\n\n",
            new string('甲', 10000),
            new string('乙', 10000));
        var meeting = await fixture.AddCompletedMeetingAsync("長會議", longTranscript, template);

        // 摘要全是空白字元 → 每一段都會走 continue，最後整個工作以失敗收場。
        var provider = new FakeTextGenerationProvider("   ");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);

        var ledger = await fixture.ReadUsageLogAsync();

        // 兩段摘要都打了 API，兩筆都要在帳上，儘管最後整個工作失敗。
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, x => Assert.Equal(AiUsageFeature.MeetingDraftChunkSummary, x.Feature));
        Assert.All(ledger, x => Assert.Equal(AiUsageOutcome.Succeeded, x.Outcome));
    }

    [Fact]
    public async Task RunAsync_ShouldRecordFailedCallWithoutGuessingTokens()
    {
        // 串流跑到一半斷掉時，已經生成的 token 供應商照算，但 usage 那一片永遠不會到。
        // 所以要記一筆、但**不猜用量**——猜出來的數字會被當成事實。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider(new InvalidOperationException("連線中斷"));
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var row = Assert.Single(await fixture.ReadUsageLogAsync());

        Assert.Equal(AiUsageOutcome.Failed, row.Outcome);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.EstimatedCost);
        Assert.Contains("連線中斷", row.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_RecordingShouldNotCommitDraftBeforeItIsReady()
    {
        // ⚠️ Recorder 與 runner 共用同一個 BackendDBContext，它的 SaveChanges 會把
        // runner 所有未提交的追蹤變更一起送出去。所以記帳必須放在「修改 meeting 之前」；
        // 放在後面的話，生成失敗時反而會把半成品的草稿寫進資料庫。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider(new InvalidOperationException("連線中斷"));
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);

        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);
        Assert.Null(saved.DraftContent);
    }

    [Fact]
    public async Task RunAsync_ShouldAttributeToTheRequester()
    {
        // ⚠️ 背景 scope 的 CurrentUserService 是空白物件（Id=0、Name=""），不是 null。
        // 觸發者一定要一路從佇列傳進來，否則所有背景呼叫都會被記到「空白使用者」而且不報錯。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider("會議紀錄");
        await fixture.CreateRunner(provider).RunAsync(
            meeting.Id, CancellationToken.None, isCancelledByUser: null,
            requestedByUserId: 7, requestedByUserName: "王小明");

        var row = Assert.Single(await fixture.ReadUsageLogAsync());

        Assert.Equal(7, row.UserId);
        Assert.Equal("王小明", row.UserName);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordWithoutCost_WhenProviderReportsNoUsage()
    {
        // api-version 不支援 stream_options 時會走到這裡。呼叫成功、但沒有用量可記——
        // 金額必須留白而不是 0，否則總額會靜靜地少報。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("週會", "內容", template);

        var provider = new FakeTextGenerationProvider("會議紀錄") { Usage = null };
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var row = Assert.Single(await fixture.ReadUsageLogAsync());

        Assert.Equal(AiUsageOutcome.Succeeded, row.Outcome);
        Assert.Null(row.InputTokens);
        Assert.Null(row.EstimatedCost);
    }

    #endregion

    #region 失敗與跳過

    [Fact]
    public async Task RunAsync_ShouldMarkFailedWithMessage_WhenProviderThrows()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", "內容", template);

        var provider = new FakeTextGenerationProvider(new InvalidOperationException("文字生成 API 回應 429"));
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);
        Assert.Contains("429", saved.DraftError);
        Assert.NotNull(saved.DraftCompletedAt);
    }

    [Fact]
    public async Task RunAsync_ShouldMarkFailed_WhenPromptTemplateWasDeleted()
    {
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", "內容", template);

        fixture.Context.PromptTemplate.Remove(await fixture.Context.PromptTemplate.FirstAsync(x => x.Id == template.Id));
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateRunner(new FakeTextGenerationProvider("不會被呼叫")).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);
        Assert.Contains("提示詞範本", saved.DraftError);
    }

    [Fact]
    public async Task RunAsync_ShouldMarkFailed_WhenTranscriptIsNotCompleted()
    {
        // 不能停在「待處理」不動，否則這筆會永遠卡著沒人接手。
        await using var fixture = await DraftJobFixture.CreateAsync();
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("尚未轉錄", "內容", template);

        var tracked = await fixture.Context.Meeting.FirstAsync(x => x.Id == meeting.Id);
        tracked.TranscriptionStatus = TranscriptionStatus.Pending;
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var provider = new FakeTextGenerationProvider("不會被呼叫");
        await fixture.CreateRunner(provider).RunAsync(meeting.Id, CancellationToken.None);

        Assert.Empty(provider.Calls);
        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);
        Assert.Contains("尚未轉錄完成", saved.DraftError);
    }

    [Fact]
    public async Task RunAsync_ShouldMarkFailed_WhenConfiguredProviderIsNotRegistered()
    {
        await using var fixture = await DraftJobFixture.CreateAsync(providerName: "SomeOtherVendor");
        var template = await fixture.AddTemplateAsync("整理：{{transcript}}");
        var meeting = await fixture.AddCompletedMeetingAsync("需求確認會議", "內容", template);

        await fixture.CreateRunner(new FakeTextGenerationProvider("內容")).RunAsync(meeting.Id, CancellationToken.None);

        var saved = await fixture.Context.Meeting.AsNoTracking().FirstAsync(x => x.Id == meeting.Id);
        Assert.Equal(DraftStatus.Failed, saved.DraftStatus);
        Assert.Contains("SomeOtherVendor", saved.DraftError);
    }

    #endregion

    #region 測試輔助

    private sealed record GenerateCall(string SystemPrompt, string UserPrompt);

    private sealed class FakeTextGenerationProvider : ITextGenerationProvider
    {
        private readonly string? response;
        private readonly Exception? failure;

        public FakeTextGenerationProvider(string response) => this.response = response;

        public FakeTextGenerationProvider(Exception failure) => this.failure = failure;

        public List<GenerateCall> Calls { get; } = [];

        public string ProviderName => "AzureOpenAI";

        public string ModelName => "gpt-4o-mini";

        /// <summary>每次呼叫回報的 token 用量。設成 null 可模擬「供應商沒回報用量」。</summary>
        public AiTokenUsage? Usage { get; set; } = new(InputTokens: 100, OutputTokens: 50);

        public Task<TextGenerationResult> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            Action<string>? onDelta,
            CancellationToken cancellationToken,
            IReadOnlyList<PromptImage>? images = null)
        {
            Calls.Add(new GenerateCall(systemPrompt, userPrompt));

            if (failure is not null)
            {
                throw failure;
            }

            // 模擬串流：拆成兩段回報，這樣 runner 的「自己累加長度」才真的被測到
            // ——一次全給的話，累加寫錯也看不出來。
            var midpoint = response!.Length / 2;
            onDelta?.Invoke(response[..midpoint]);
            onDelta?.Invoke(response[midpoint..]);

            return Task.FromResult(new TextGenerationResult(response, Usage));
        }
    }

    private sealed class DraftJobFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly string rootPath;
        private readonly MeetingFileStore fileStore;
        private readonly IOptions<LlmSettings> llmSettings;

        private DraftJobFixture(SqliteConnection connection, BackendDBContext context, string rootPath, string providerName)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });

            var systemSettings = new SystemSettings();
            systemSettings.ExternalFileSystem.MeetingMediaPath = Path.Combine(rootPath, "media");
            systemSettings.ExternalFileSystem.MeetingTranscriptPath = TranscriptRoot;

            fileStore = new MeetingFileStore(
                Options.Create(systemSettings),
                loggerFactory.CreateLogger<MeetingFileStore>());

            var settings = new LlmSettings { DefaultProvider = providerName };
            settings.Providers[providerName] = new LlmProviderSettings
            {
                Endpoint = "https://contoso.openai.azure.com/",
                ApiKey = "test-key",
                Model = "gpt-4o-mini",
                ApiVersion = "2024-10-21",
            };

            // 單價設好，帳本的金額欄位才測得到；沒設的話金額一律是 null（那是另一組測試在守的）。
            settings.Pricing["gpt-4o-mini"] = new LlmPricingSettings
            {
                InputPerMillionTokens = 0.15m,
                OutputPerMillionTokens = 0.60m,
            };

            llmSettings = Options.Create(settings);
        }

        public BackendDBContext Context { get; }

        public string TranscriptRoot => Path.Combine(rootPath, "transcript");

        public static async Task<DraftJobFixture> CreateAsync(string providerName = "AzureOpenAI")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            return new DraftJobFixture(connection, context, rootPath, providerName);
        }

        /// <summary>進度通知器沒有外部相依，直接用真的。</summary>
        public MeetingDraftProgressNotifier ProgressNotifier { get; } = new();

        public MeetingDraftJobRunner CreateRunner(ITextGenerationProvider provider)
        {
            return new MeetingDraftJobRunner(
                Context,
                [provider],
                fileStore,
                llmSettings,
                ProgressNotifier,
                // 用真的 Recorder 而不是假的：它與 runner 共用同一個 context，
                // 而「記帳的 SaveChanges 會不會提前提交 runner 的變更」正是要測的事情之一。
                new AiUsageRecorder(Context, llmSettings, new ExchangeRateCache(), loggerFactory.CreateLogger<AiUsageRecorder>()),
                loggerFactory.CreateLogger<MeetingDraftJobRunner>());
        }

        /// <summary>這個 fixture 的帳本內容，依寫入順序。</summary>
        public async Task<List<AiUsageLog>> ReadUsageLogAsync()
            => await Context.AiUsageLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();

        public async Task<PromptTemplate> AddTemplateAsync(string content)
        {
            var template = new PromptTemplate
            {
                Name = "測試提示詞",
                Content = content,
                IsEnabled = true,
            };

            Context.PromptTemplate.Add(template);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return template;
        }

        /// <summary>
        /// 新增一筆轉錄完成、等待生成的會議。
        /// 名單相關的兩個參數刻意做成可選，既有 12 處呼叫不用改。
        /// </summary>
        public async Task<Meeting> AddCompletedMeetingAsync(
            string title,
            string transcript,
            PromptTemplate promptTemplate,
            DateTime? meetingDate = null,
            string? glossaryTerms = null,
            string? attendees = null)
        {
            var relativePath = $"2026/08/{Guid.NewGuid():N}.txt";
            var fullPath = Path.Combine(TranscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, transcript, new UTF8Encoding(true));

            int? projectId = null;
            if (glossaryTerms is not null)
            {
                var project = new Project
                {
                    Title = "測試專案",
                    Status = "進行中",
                    Owner = "測試人",
                    GlossaryTerms = glossaryTerms,
                };
                Context.Project.Add(project);
                await Context.SaveChangesAsync();
                projectId = project.Id;
            }

            var meeting = new Meeting
            {
                Title = title,
                MeetingDate = meetingDate,
                TranscriptionStatus = TranscriptionStatus.Completed,
                TranscriptRelativePath = relativePath,
                ProjectId = projectId,
                DraftPromptTemplateId = promptTemplate.Id,
                DraftPromptTemplateName = promptTemplate.Name,
                DraftAttendees = attendees,
                DraftStatus = DraftStatus.Pending,
            };

            Context.Meeting.Add(meeting);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return meeting;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試暫存目錄清不掉不影響結果。
            }
        }
    }

    #endregion
}
