using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class TodoServiceTests
{
    #region 資料往返與標籤字串轉換

    [Fact]
    public async Task AddAsync_ShouldPersistFieldsAndTagStrings()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var service = fixture.CreateService();

        var result = await service.AddAsync(new TodoAdapterModel
        {
            Title = "補上轉錄失敗的自動重試",
            Description = "檔案一多就沒人管",
            ProjectId = project.Id,
            Owner = "陳大文",
            DueDate = new DateTime(2026, 9, 10),
            Priority = "高",
            Status = "待辦",
            Categories = ["工程"],
            Teams = ["團隊A"],
        });

        Assert.True(result.Success);

        var saved = await fixture.Context.Todo.AsNoTracking().SingleAsync();
        Assert.Equal("補上轉錄失敗的自動重試", saved.Title);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Null(saved.MeetingId);
        Assert.Equal("高", saved.Priority);
        // 標籤在資料庫是換行包夾的分隔字串
        Assert.Equal(TagStringHelper.ToStored(["工程"]), saved.Categories);
        Assert.Equal(TagStringHelper.ToStored(["團隊A"]), saved.Teams);
    }

    [Fact]
    public async Task GetAsync_ShouldMapTagsBackToListsAndFillRelatedTitles()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var meeting = await fixture.AddMeetingAsync("需求確認會議");
        var todo = await fixture.AddTodoAsync("整理客戶回饋", project.Id, meetingId: meeting.Id, categories: ["工程", "客戶"]);
        var service = fixture.CreateService();

        var loaded = await service.GetAsync(todo.Id);

        Assert.Equal(["工程", "客戶"], loaded.Categories);
        Assert.Equal("Q3 產品改版專案", loaded.ProjectTitle);
        Assert.Equal("需求確認會議", loaded.MeetingTitle);
        Assert.Equal("需求確認會議", loaded.SourceText);
    }

    [Fact]
    public async Task SourceText_ShouldIndicateManualEntry_WhenNoMeeting()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var todo = await fixture.AddTodoAsync("手動建立的待辦", project.Id);
        var service = fixture.CreateService();

        var loaded = await service.GetAsync(todo.Id);

        Assert.Null(loaded.MeetingTitle);
        Assert.Equal("— 手動新增", loaded.SourceText);
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotOverwriteSourceMeeting()
    {
        // 來源會議紀錄由系統寫入，畫面上的舊複本不得把它清掉。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var meeting = await fixture.AddMeetingAsync("需求確認會議");
        var todo = await fixture.AddTodoAsync("由 AI 抽出的待辦", project.Id, meetingId: meeting.Id);
        var service = fixture.CreateService();

        var stale = await service.GetAsync(todo.Id);
        stale.Title = "改過標題";
        stale.MeetingId = null;

        var result = await service.UpdateAsync(stale);

        Assert.True(result.Success);
        var saved = await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id);
        Assert.Equal("改過標題", saved.Title);
        Assert.Equal(meeting.Id, saved.MeetingId);
    }

    #endregion

    #region 完成狀態

    [Fact]
    public async Task SetCompletedAsync_ShouldWriteOnlyStatus()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var todo = await fixture.AddTodoAsync("待辦一", project.Id, status: "待辦");
        var service = fixture.CreateService();

        var result = await service.SetCompletedAsync(todo.Id, true);

        Assert.True(result.Success);
        var saved = await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id);
        Assert.Equal("已完成", saved.Status);
    }

    [Fact]
    public async Task SetCompletedAsync_ShouldFallBackToInProgress_WhenUnchecked()
    {
        // 取消完成退回「進行中」——已經動過的事情退回未開始並不合理。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var todo = await fixture.AddTodoAsync("待辦一", project.Id, status: "已完成");
        var service = fixture.CreateService();

        await service.SetCompletedAsync(todo.Id, false);

        var saved = await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id);
        Assert.Equal("進行中", saved.Status);
    }

    [Fact]
    public async Task SetCompletedAsync_ShouldReject_WhenOutOfTeamScope()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var todo = await fixture.AddTodoAsync("團隊B的待辦", project.Id, teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.SetCompletedAsync(todo.Id, true);

        Assert.False(result.Success);
        var saved = await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id);
        Assert.NotEqual("已完成", saved.Status);
    }

    #endregion

    #region 逾期判斷

    [Fact]
    public void IsOverdue_ShouldBeTrue_ForPastDueUnfinishedTodo()
    {
        var todo = new TodoAdapterModel { DueDate = DateTime.Today.AddDays(-2), Status = "待辦" };

        Assert.True(todo.IsOverdue);
        Assert.Equal(2, todo.OverdueDays);
        Assert.Contains("逾期 2 天", todo.DueDateText);
    }

    [Fact]
    public void IsOverdue_ShouldBeFalse_WhenCompleted()
    {
        // 已完成的事情不算逾期，否則清單會被歷史資料洗版。
        var todo = new TodoAdapterModel { DueDate = DateTime.Today.AddDays(-2), Status = "已完成" };

        Assert.False(todo.IsOverdue);
        Assert.DoesNotContain("逾期", todo.DueDateText);
    }

    [Fact]
    public void IsOverdue_ShouldBeFalse_WhenNoDueDate()
    {
        var todo = new TodoAdapterModel { DueDate = null, Status = "待辦" };

        Assert.False(todo.IsOverdue);
        Assert.Equal("未指定", todo.DueDateText);
    }

    [Fact]
    public void Clone_ShouldDeepCopyTagLists()
    {
        // 淺複製會讓編輯中的修改回寫到清單資料列（MyTask 當年就是這個 bug）。
        var todo = new TodoAdapterModel { Categories = ["工程"], Teams = ["團隊A"] };

        var cloned = todo.Clone();
        cloned.Categories.Add("客戶");

        Assert.Single(todo.Categories);
        Assert.Equal(2, cloned.Categories.Count);
        Assert.NotSame(todo.Teams, cloned.Teams);
    }

    #endregion

    #region 過濾與團隊可見性

    [Fact]
    public async Task GetAsync_ShouldFilterByProject()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var projectA = await fixture.AddProjectAsync("專案A");
        var projectB = await fixture.AddProjectAsync("專案B");
        await fixture.AddTodoAsync("A 的待辦", projectA.Id);
        await fixture.AddTodoAsync("B 的待辦", projectB.Id);
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest(projectFilter: projectA.Id));

        Assert.Equal(1, result.Count);
        Assert.Equal("A 的待辦", result.Result.Single().Title);
    }

    [Fact]
    public async Task GetAsync_ShouldFilterByStatus()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("待辦一", project.Id, status: "待辦");
        await fixture.AddTodoAsync("已完成的", project.Id, status: "已完成");
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest(statusFilter: "已完成"));

        Assert.Equal(1, result.Count);
        Assert.Equal("已完成的", result.Result.Single().Title);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("公開待辦", project.Id);
        await fixture.AddTodoAsync("團隊A待辦", project.Id, teams: ["團隊A"]);
        await fixture.AddTodoAsync("團隊B待辦", project.Id, teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result.Result, x => x.Title == "團隊B待辦");
    }

    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("公開待辦", project.Id);
        await fixture.AddTodoAsync("團隊A待辦", project.Id, teams: ["團隊A"]);
        await fixture.AddTodoAsync("團隊B待辦", project.Id, teams: ["團隊B"]);
        var service = fixture.CreateService();

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_ById_ShouldReturnEmptyModel_WhenOutOfTeamScope()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var todo = await fixture.AddTodoAsync("團隊B待辦", project.Id, teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var loaded = await service.GetAsync(todo.Id);

        Assert.Equal(0, loaded.Id);
    }

    #endregion

    #region 前置檢查

    [Fact]
    public async Task BeforeAddCheckAsync_ShouldFail_WhenProjectDoesNotExist()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TodoAdapterModel { Title = "孤兒待辦", ProjectId = 999 });

        Assert.False(result.Success);
        Assert.Contains("專案", result.Message);
    }

    #endregion

    #region 測試輔助

    private static DataRequest NewRequest(int? projectFilter = null, string? statusFilter = null) => new()
    {
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
        ProjectFilter = projectFilter,
        StatusFilter = statusFilter,
    };

    private sealed class FakeScopeProvider(bool isAdmin, IReadOnlyList<string> teams) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }

    private sealed class TodoServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private TodoServiceFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<TodoServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new TodoServiceFixture(connection, context);
        }

        public TodoService CreateService(bool isAdmin = true, params string[] teams)
        {
            return new TodoService(
                Context,
                mapper,
                loggerFactory.CreateLogger<TodoService>(),
                new FakeScopeProvider(isAdmin, teams));
        }

        public async Task<Project> AddProjectAsync(string title)
        {
            var project = new Project
            {
                Title = title,
                Status = "進行中",
                Owner = "王小明",
            };

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

        public async Task<Todo> AddTodoAsync(
            string title,
            int projectId,
            int? meetingId = null,
            string status = "待辦",
            IEnumerable<string>? categories = null,
            IEnumerable<string>? teams = null)
        {
            var todo = new Todo
            {
                Title = title,
                ProjectId = projectId,
                MeetingId = meetingId,
                Status = status,
                Categories = TagStringHelper.ToStored(categories),
                Teams = TagStringHelper.ToStored(teams),
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

    #endregion
}
