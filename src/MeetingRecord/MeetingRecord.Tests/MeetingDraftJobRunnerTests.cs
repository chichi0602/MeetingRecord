using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
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

        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            Calls.Add(new GenerateCall(systemPrompt, userPrompt));

            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(response!);
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
                loggerFactory.CreateLogger<MeetingDraftJobRunner>());
        }

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

        public async Task<Meeting> AddCompletedMeetingAsync(
            string title,
            string transcript,
            PromptTemplate promptTemplate,
            DateTime? meetingDate = null)
        {
            var relativePath = $"2026/08/{Guid.NewGuid():N}.txt";
            var fullPath = Path.Combine(TranscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, transcript, new UTF8Encoding(true));

            var meeting = new Meeting
            {
                Title = title,
                MeetingDate = meetingDate,
                TranscriptionStatus = TranscriptionStatus.Completed,
                TranscriptRelativePath = relativePath,
                DraftPromptTemplateId = promptTemplate.Id,
                DraftPromptTemplateName = promptTemplate.Name,
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
