using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 儀表板服務的測試。
///
/// <para>
/// <c>DashboardMetricsTests</c> 測的是純函式，但有幾件事<b>只有在服務層才看得出來</b>：
/// 投影有沒有真的把欄位撈回來（漏欄位是「存得進去、算不出來」的無聲錯誤）、
/// 團隊過濾有沒有照既有規則套、以及卡片與細分列是不是同一個數字。
/// </para>
/// </summary>
public sealed class DashboardServiceTests
{
    #region 提示詞範本

    [Fact]
    public async Task PromptTemplates_ShouldSplitEnabledAndDisabled()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddPromptTemplateAsync("標準會議紀錄", isEnabled: true);
        await fixture.AddPromptTemplateAsync("客戶訪談紀要", isEnabled: true);
        await fixture.AddPromptTemplateAsync("舊版格式", isEnabled: false);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(3, summary.PromptTemplates.TotalCount);
        Assert.Equal(2, summary.PromptTemplates.EnabledCount);
        Assert.Equal(1, summary.PromptTemplates.DisabledCount);
    }

    [Fact]
    public async Task PromptTemplates_EnabledPlusDisabled_ShouldEqualTotal()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddPromptTemplateAsync("甲", isEnabled: true);
        await fixture.AddPromptTemplateAsync("乙", isEnabled: false);
        await fixture.AddPromptTemplateAsync("丙", isEnabled: false);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(
            summary.PromptTemplates.TotalCount,
            summary.PromptTemplates.EnabledCount + summary.PromptTemplates.DisabledCount);
    }

    [Fact]
    public async Task PromptTemplates_ShouldCountUnusedAgainstRealMeetings()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddPromptTemplateAsync("有人用的", isEnabled: true);
        await fixture.AddPromptTemplateAsync("沒人用的", isEnabled: true);
        await fixture.AddMeetingAsync("會議一", draftTemplateName: "有人用的");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(1, summary.PromptTemplates.UnusedEnabledCount);
    }

    [Fact]
    public async Task PromptTemplates_ShouldRespectTeamScope()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddPromptTemplateAsync("團隊A的範本", isEnabled: true, teams: "\n團隊A\n");
        await fixture.AddPromptTemplateAsync("團隊B的範本", isEnabled: true, teams: "\n團隊B\n");

        // 提示詞範本頁對非管理員就是套團隊過濾的，儀表板不跟著做
        // 就會出現「儀表板說有 2 個、點進去只看得到 1 個」。
        var scoped = await fixture.CreateService(isAdmin: false, teams: ["團隊A"]).GetSummaryAsync();
        var admin = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(1, scoped.PromptTemplates.TotalCount);
        Assert.Equal(2, admin.PromptTemplates.TotalCount);
    }

    [Fact]
    public async Task PromptTemplates_NoTemplates_ShouldReturnZeros()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(0, summary.PromptTemplates.TotalCount);
        Assert.Equal(0, summary.PromptTemplates.EnabledCount);
        Assert.Equal(0, summary.PromptTemplates.DisabledCount);
        Assert.Equal(0, summary.PromptTemplates.UnusedEnabledCount);
    }

    #endregion

    #region 儲存空間

    [Fact]
    public async Task Storage_ShouldSumMediaFileSizes()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddMeetingAsync("會議一", mediaFileSize: 1000);
        await fixture.AddMeetingAsync("會議二", mediaFileSize: 2000);
        await fixture.AddMeetingAsync("沒有影音檔", mediaFileSize: null);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(3000L, summary.Storage.MediaBytes);
    }

    [Fact]
    public async Task Storage_ShouldCountProjectFileSizes()
    {
        // 這筆守的是「投影漏了 ProjectFile 查詢」這種無聲錯誤：
        // 附件明明存得進去，儀表板卻一直顯示 0。
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        await fixture.AddProjectFileAsync(projectId, 4096);
        await fixture.AddProjectFileAsync(projectId, 2048);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(6144L, summary.Storage.AttachmentBytes);
    }

    [Fact]
    public async Task Storage_ShouldMeasureTranscriptDirectory()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        fixture.WriteTranscriptFile("2026/09/a.txt", 120);
        fixture.WriteTranscriptFile("2026/09/b.txt", 80);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(200L, summary.Storage.TranscriptBytes);
    }

    [Fact]
    public async Task Storage_MissingTranscriptDirectory_ShouldNotThrow()
    {
        // 全新環境還沒有任何逐字稿，儀表板仍要開得起來。
        await using var fixture = await DashboardServiceFixture.CreateAsync();

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(0L, summary.Storage.TranscriptBytes);
    }

    [Fact]
    public async Task Storage_TotalShouldEqualSumOfParts()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        await fixture.AddMeetingAsync("會議一", mediaFileSize: 500);
        await fixture.AddProjectFileAsync(projectId, 300);
        fixture.WriteTranscriptFile("t.txt", 200);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(1000L, summary.Storage.TotalBytes);
        Assert.Equal(
            summary.Storage.MediaBytes + summary.Storage.TranscriptBytes + summary.Storage.AttachmentBytes,
            summary.Storage.TotalBytes);
    }

    [Fact]
    public async Task StorageCard_ShouldShowTotalNotMediaOnly()
    {
        // 卡片與細分列必須同源，否則畫面上兩個數字看起來像在打架。
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        await fixture.AddMeetingAsync("會議一", mediaFileSize: 1024);
        await fixture.AddProjectFileAsync(projectId, 1024);

        var summary = await fixture.CreateService().GetSummaryAsync();

        var card = summary.Cards.Single(x => x.Title == "儲存空間");
        Assert.Equal("2 KB", card.Value);
    }

    #endregion

    private sealed class DashboardServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly IOptions<SystemSettings> systemSettings;
        private readonly string rootPath;
        private readonly string transcriptPath;

        private DashboardServiceFixture(SqliteConnection connection, BackendDBContext context, string rootPath)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });

            transcriptPath = Path.Combine(rootPath, "transcript");

            var settings = new SystemSettings();
            settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "aichat");
            settings.ExternalFileSystem.MeetingTranscriptPath = transcriptPath;
            systemSettings = Options.Create(settings);
        }

        public BackendDBContext Context { get; }

        public static async Task<DashboardServiceFixture> CreateAsync()
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

            return new DashboardServiceFixture(connection, context, rootPath);
        }

        public DashboardService CreateService(bool isAdmin = true, IReadOnlyList<string>? teams = null)
        {
            var chatStore = new AiChatStore(systemSettings, loggerFactory.CreateLogger<AiChatStore>());

            return new DashboardService(
                Context,
                new FakeDashboardScopeProvider(isAdmin, teams ?? []),
                chatStore,
                systemSettings,
                loggerFactory.CreateLogger<DashboardService>());
        }

        public async Task AddPromptTemplateAsync(string name, bool isEnabled, string? teams = null)
        {
            await Context.PromptTemplate.AddAsync(new PromptTemplate
            {
                Name = name,
                Content = "內容",
                IsEnabled = isEnabled,
                Teams = teams,
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task<int> AddProjectAsync(string title)
        {
            var project = new Project { Title = title, Status = "進行中" };
            await Context.Project.AddAsync(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return project.Id;
        }

        public async Task AddProjectFileAsync(int projectId, long fileSize)
        {
            await Context.ProjectFile.AddAsync(new ProjectFile
            {
                ProjectId = projectId,
                OriginalFileName = "a.pdf",
                StoredFileName = $"{Guid.NewGuid():N}.pdf",
                RelativePath = "2026/09/a.pdf",
                FileSize = fileSize,
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task AddMeetingAsync(
            string title,
            long? mediaFileSize = null,
            string? draftTemplateName = null,
            string? teams = null)
        {
            await Context.Meeting.AddAsync(new Meeting
            {
                Title = title,
                MediaFileSize = mediaFileSize,
                DraftPromptTemplateName = draftTemplateName,
                DraftStatus = draftTemplateName is null ? DraftStatus.NotGenerated : DraftStatus.Completed,
                TranscriptionStatus = TranscriptionStatus.Completed,
                Teams = teams,
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public void WriteTranscriptFile(string relativePath, int byteCount)
        {
            var fullPath = Path.Combine(transcriptPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, new byte[byteCount]);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // 測試用的暫存目錄清不掉不該讓測試失敗。
            }
        }
    }

    private sealed class FakeDashboardScopeProvider(bool isAdmin, IReadOnlyList<string> teams)
        : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }
}
