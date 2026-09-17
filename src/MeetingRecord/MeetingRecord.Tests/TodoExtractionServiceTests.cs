using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.TodoExtraction;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 抽出待辦的服務層測試。
///
/// <para>
/// 這裡只測 <c>GetExistingTitlesAsync</c>——那是「按兩次會不會重複」的判斷依據。
/// 抽取本身要呼叫 AI，不在單元測試範圍（提示詞與解析已由 <c>TodoExtractionParserTests</c> 涵蓋）。
/// </para>
///
/// <para>
/// ⚠️ 重複只影響候選的<b>預設勾選狀態</b>，服務層<b>刻意不擋</b>重複寫入——
/// 使用者可以勾回去再加一次。所以這裡沒有、也不該有「重複時 AddAsync 要失敗」的測試。
/// </para>
/// </summary>
public sealed class TodoExtractionServiceTests
{
    [Fact]
    public async Task GetExistingTitles_ShouldReturnTitlesOfThatMeetingOnly()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案一");
        var meetingA = await fixture.AddMeetingAsync("九月第一週會議");
        var meetingB = await fixture.AddMeetingAsync("九月第二週會議");

        await fixture.AddTodoAsync("完成報價單", project.Id, meetingA.Id);
        await fixture.AddTodoAsync("別場會議的事", project.Id, meetingB.Id);

        var titles = await fixture.CreateService().GetExistingTitlesAsync(meetingA.Id);

        Assert.Equal(["完成報價單"], titles);
    }

    [Fact]
    public async Task GetExistingTitles_ShouldIgnoreCaseAndWhitespace()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案一");
        var meeting = await fixture.AddMeetingAsync("會議");

        // 寫入端存的是 candidate.Title.Trim()，但既有資料可能是別的路徑手動建的。
        await fixture.AddTodoAsync("  Review PR  ", project.Id, meeting.Id);

        var titles = await fixture.CreateService().GetExistingTitlesAsync(meeting.Id);

        Assert.Contains("review pr", titles);
        Assert.Contains("REVIEW PR", titles);
    }

    [Fact]
    public async Task GetExistingTitles_ShouldIgnoreTodosWithoutMeeting()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案一");
        var meeting = await fixture.AddMeetingAsync("會議");

        // 手動新增的待辦 MeetingId 是 null，不該參與比對——
        // 否則同一個專案裡手動建過的事項會讓 AI 抽到的候選被誤標成重複。
        await fixture.AddTodoAsync("手動建立的待辦", project.Id, meetingId: null);

        var titles = await fixture.CreateService().GetExistingTitlesAsync(meeting.Id);

        Assert.Empty(titles);
    }

    [Fact]
    public async Task GetExistingTitles_NoTodos_ShouldReturnEmpty()
    {
        await using var fixture = await ExtractionFixture.CreateAsync();
        var meeting = await fixture.AddMeetingAsync("第一次抽的會議");

        var titles = await fixture.CreateService().GetExistingTitlesAsync(meeting.Id);

        Assert.Empty(titles);
    }

    [Fact]
    public async Task GetExistingTitles_ShouldDeduplicateSameTitle()
    {
        // 這個功能上線前加進去的重複資料仍然存在，集合要把它們併成一條，
        // 否則提示文字的「先前已加入 N 條」會與畫面上的標示數對不起來。
        await using var fixture = await ExtractionFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案一");
        var meeting = await fixture.AddMeetingAsync("被抽過兩次的會議");

        await fixture.AddTodoAsync("完成報價單", project.Id, meeting.Id);
        await fixture.AddTodoAsync("完成報價單", project.Id, meeting.Id);

        var titles = await fixture.CreateService().GetExistingTitlesAsync(meeting.Id);

        Assert.Single(titles);
    }

    private sealed class ExtractionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private ExtractionFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public BackendDBContext Context { get; }

        public static async Task<ExtractionFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new ExtractionFixture(connection, context);
        }

        /// <summary>
        /// 這些測試只走 <c>GetExistingTitlesAsync</c>，不會呼叫模型，
        /// 所以 provider 給空集合、設定給預設值即可。
        /// </summary>
        public TodoExtractionService CreateService()
        {
            var settings = Options.Create(new LlmSettings());

            return new TodoExtractionService(
                Context,
                [],
                settings,
                new AiUsageRecorder(Context, settings, loggerFactory.CreateLogger<AiUsageRecorder>()),
                new CurrentUserService(),
                loggerFactory.CreateLogger<TodoExtractionService>());
        }

        public async Task<Project> AddProjectAsync(string title)
        {
            var project = new Project { Title = title, Status = "進行中" };
            Context.Project.Add(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return project;
        }

        public async Task<Meeting> AddMeetingAsync(string title)
        {
            var meeting = new Meeting { Title = title };
            Context.Meeting.Add(meeting);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return meeting;
        }

        public async Task<Todo> AddTodoAsync(string title, int projectId, int? meetingId)
        {
            var todo = new Todo
            {
                Title = title,
                ProjectId = projectId,
                MeetingId = meetingId,
                Status = "待辦",
            };

            Context.Todo.Add(todo);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return todo;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
