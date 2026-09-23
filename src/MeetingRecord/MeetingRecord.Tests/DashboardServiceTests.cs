using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Dashboard;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 儀表板服務的測試。
///
/// <para>
/// <c>DashboardMetricsTests</c> 測的是純函式，但有幾件事<b>只有在服務層才看得出來</b>：
/// 投影有沒有真的把欄位撈回來（漏欄位是「存得進去、算不出來」的無聲錯誤）、
/// 儀表板有沒有維持「不套團隊過濾」、以及卡片與細分列是不是同一個數字。
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
    public async Task PromptTemplatesAndMeetings_ShouldCountAllTeams()
    {
        // 0.4.97 起儀表板是全公司同一份數字，刻意不套團隊過濾（服務也不再注入存取範圍）。
        // 這筆守的是有人「順手」把過濾加回來：那會讓每個人看到不同的數字。
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddPromptTemplateAsync("團隊A的範本", isEnabled: true, teams: "\n團隊A\n");
        await fixture.AddPromptTemplateAsync("團隊B的範本", isEnabled: true, teams: "\n團隊B\n");
        await fixture.AddMeetingAsync("團隊A的會議", teams: "\n團隊A\n");
        await fixture.AddMeetingAsync("公開會議");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(2, summary.PromptTemplates.TotalCount);
        Assert.Equal("2", summary.Cards.Single(x => x.Title == "會議紀錄").Value);
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

    #region 待辦概況

    [Fact]
    public async Task Todos_ShouldSplitOverdueAndDueSoonAtBoundaries()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        var today = DateTime.Today;
        await fixture.AddTodoAsync(projectId, dueDate: today.AddDays(-1));                    // 逾期
        await fixture.AddTodoAsync(projectId, dueDate: today.AddDays(-3), status: "已完成");  // 完成了就不算逾期
        await fixture.AddTodoAsync(projectId, dueDate: today);                                // 今天到期＝7 天內，不是逾期
        await fixture.AddTodoAsync(projectId, dueDate: today.AddDays(7));                     // 第 7 天仍算
        await fixture.AddTodoAsync(projectId, dueDate: today.AddDays(8));                     // 超出
        await fixture.AddTodoAsync(projectId, dueDate: null, status: "進行中");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(1, summary.Todos.OverdueCount);
        Assert.Equal(2, summary.Todos.DueSoonCount);
        Assert.Equal(1, summary.Todos.InProgressCount);
    }

    [Fact]
    public async Task Todos_FromMeetingRate_ShouldCountTodosLinkedToMeetings()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        var meetingId = await fixture.AddMeetingAsync("會議一");
        await fixture.AddTodoAsync(projectId, meetingId: meetingId);
        await fixture.AddTodoAsync(projectId);
        await fixture.AddTodoAsync(projectId);
        await fixture.AddTodoAsync(projectId);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(25d, summary.Todos.FromMeetingRate);
    }

    [Fact]
    public async Task Todos_NoTodos_FromMeetingRateShouldBeNull()
    {
        // null 讓畫面顯示「—」；0% 會被讀成「全部都是手動建立的」。
        await using var fixture = await DashboardServiceFixture.CreateAsync();

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Null(summary.Todos.FromMeetingRate);
    }

    [Fact]
    public async Task OpenTodoPriority_ShouldExcludeCompletedAndEmptySlices()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var projectId = await fixture.AddProjectAsync("專案一");
        await fixture.AddTodoAsync(projectId, priority: "高");
        await fixture.AddTodoAsync(projectId, priority: "高");
        await fixture.AddTodoAsync(projectId, priority: "中");
        await fixture.AddTodoAsync(projectId, priority: "低", status: "已完成");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(
            [("高", 2), ("中", 1)],
            summary.OpenTodoPriority.Select(x => (x.Label, x.Value)).ToList());
    }

    #endregion

    #region 專案概況

    [Fact]
    public async Task Projects_ShouldCountOverdueDueSoonAndOnHold()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var today = DateTime.Today;
        await fixture.AddProjectAsync("逾期", endDate: today.AddDays(-1));
        await fixture.AddProjectAsync("逾期但已完成", status: "已完成", endDate: today.AddDays(-1));
        await fixture.AddProjectAsync("第 14 天", endDate: today.AddDays(14));
        await fixture.AddProjectAsync("第 15 天", endDate: today.AddDays(15));
        await fixture.AddProjectAsync("暫緩", status: "暫緩");
        await fixture.AddProjectAsync("等待", status: "等待");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(1, summary.Projects.OverdueCount);
        Assert.Equal(1, summary.Projects.DueSoonCount);
        Assert.Equal(2, summary.Projects.OnHoldCount);
    }

    [Fact]
    public async Task Projects_AverageCompletion_ShouldOnlyCountInProgress()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddProjectAsync("甲", completion: 20);
        await fixture.AddProjectAsync("乙", completion: 60);
        await fixture.AddProjectAsync("已完成的不拉高平均", status: "已完成", completion: 100);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal(40d, summary.Projects.AverageInProgressCompletion);
    }

    #endregion

    #region 會議時數

    [Fact]
    public async Task MeetingHours_ShouldOnlyCountLatestTranscriptionRun()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var latestStart = DateTime.Now.AddHours(-1);
        var meetingId = await fixture.AddMeetingAsync("重轉錄過的會議", transcriptionStartedAt: latestStart);

        // 第一輪（最後一輪開始之前）兩段各 30 分鐘，重轉錄後又是同樣兩段。
        // 全部加總會變成 2 小時；會議實際只有 1 小時。
        await fixture.AddTranscriptionUsageAsync(meetingId, latestStart.AddHours(-2), 1800);
        await fixture.AddTranscriptionUsageAsync(meetingId, latestStart.AddHours(-2), 1800);
        await fixture.AddTranscriptionUsageAsync(meetingId, latestStart.AddMinutes(5), 1800);
        await fixture.AddTranscriptionUsageAsync(meetingId, latestStart.AddMinutes(9), 1800);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal("1 小時 0 分", summary.MeetingHours.TotalDuration);
        Assert.Equal(1, summary.MeetingHours.MeasuredCount);
        Assert.Equal(1, summary.MeetingHours.TranscribedCount);
    }

    [Fact]
    public async Task MeetingHours_ShouldIgnoreFailedUsageAndUnmeasuredMeetings()
    {
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        var start = DateTime.Now.AddHours(-1);
        var measured = await fixture.AddMeetingAsync("有時長", transcriptionStartedAt: start);
        await fixture.AddMeetingAsync("0.4.80 之前轉錄的", transcriptionStartedAt: start);
        await fixture.AddTranscriptionUsageAsync(measured, start.AddMinutes(1), 600);
        await fixture.AddTranscriptionUsageAsync(measured, start.AddMinutes(2), 600, AiUsageOutcome.Failed);

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal("10 分 0 秒", summary.MeetingHours.TotalDuration);
        Assert.Equal("10 分 0 秒", summary.MeetingHours.AverageDuration);
        Assert.Equal(1, summary.MeetingHours.MeasuredCount);
        Assert.Equal(2, summary.MeetingHours.TranscribedCount);
    }

    [Fact]
    public async Task MeetingHours_NoUsage_ShouldShowDash()
    {
        // 沒有任何時長紀錄時顯示「—」而不是「0 秒」——後者會被讀成「會議都很短」。
        await using var fixture = await DashboardServiceFixture.CreateAsync();
        await fixture.AddMeetingAsync("會議一");

        var summary = await fixture.CreateService().GetSummaryAsync();

        Assert.Equal("—", summary.MeetingHours.TotalDuration);
        Assert.Equal("—", summary.MeetingHours.ThisMonthDuration);
        Assert.Equal("—", summary.MeetingHours.AverageDuration);
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

        public DashboardService CreateService()
        {
            var chatStore = new AiChatStore(systemSettings, loggerFactory.CreateLogger<AiChatStore>());

            return new DashboardService(
                Context,
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

        public async Task<int> AddProjectAsync(
            string title,
            string status = "進行中",
            DateTime? endDate = null,
            int completion = 0)
        {
            var project = new Project
            {
                Title = title,
                Status = status,
                EndDate = endDate,
                CompletionPercentage = completion,
            };
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

        public async Task<int> AddMeetingAsync(
            string title,
            long? mediaFileSize = null,
            string? draftTemplateName = null,
            string? teams = null,
            DateTime? transcriptionStartedAt = null)
        {
            var meeting = new Meeting
            {
                Title = title,
                MediaFileSize = mediaFileSize,
                DraftPromptTemplateName = draftTemplateName,
                DraftStatus = draftTemplateName is null ? DraftStatus.NotGenerated : DraftStatus.Completed,
                TranscriptionStatus = TranscriptionStatus.Completed,
                TranscriptionStartedAt = transcriptionStartedAt,
                Teams = teams,
            };
            await Context.Meeting.AddAsync(meeting);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return meeting.Id;
        }

        public async Task AddTodoAsync(
            int projectId,
            DateTime? dueDate = null,
            string status = "待辦",
            string priority = "中",
            int? meetingId = null)
        {
            await Context.Todo.AddAsync(new Todo
            {
                Title = "待辦",
                ProjectId = projectId,
                DueDate = dueDate,
                Status = status,
                Priority = priority,
                MeetingId = meetingId,
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task AddTranscriptionUsageAsync(
            int meetingId,
            DateTime occurredAt,
            double audioSeconds,
            AiUsageOutcome outcome = AiUsageOutcome.Succeeded)
        {
            await Context.AiUsageLog.AddAsync(new AiUsageLog
            {
                OccurredAt = occurredAt,
                Feature = AiUsageFeature.Transcription,
                Outcome = outcome,
                Provider = "AzureOpenAI",
                Model = "whisper",
                AudioSeconds = audioSeconds,
                MeetingId = meetingId,
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
}
