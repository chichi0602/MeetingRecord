using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class TodoServiceTests
{
    #region 資料往返

    [Fact]
    public async Task AddAsync_ShouldPersistFields()
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
        });

        Assert.True(result.Success);

        var saved = await fixture.Context.Todo.AsNoTracking().SingleAsync();
        Assert.Equal("補上轉錄失敗的自動重試", saved.Title);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Null(saved.MeetingId);
        Assert.Equal("高", saved.Priority);
    }

    [Fact]
    public async Task GetAsync_ShouldFillRelatedTitles()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("Q3 產品改版專案");
        var meeting = await fixture.AddMeetingAsync("需求確認會議");
        var todo = await fixture.AddTodoAsync("整理客戶回饋", project.Id, meetingId: meeting.Id);
        var service = fixture.CreateService();

        var loaded = await service.GetAsync(todo.Id);

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
    public void CompletionPercent_ShouldBeZero_WhenNoTodos()
    {
        // 守除零：正常路徑撈不出空群組，但這個 record 是 public 的，
        // 直接 new 一個 Total = 0 不該炸。
        var summary = new TodoOwnerSummary("甲", 0, 0, 0, 0, 0);

        Assert.Equal(0, summary.CompletionPercent);
    }

    #endregion

    #region 過濾

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

    #endregion


    #region 負責人工作量

    [Fact]
    public async Task GetOwnerSummariesAsync_ShouldGroupByOwner()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("甲的待辦一", project.Id, owner: "陳大文", status: "已完成");
        await fixture.AddTodoAsync("甲的待辦二", project.Id, owner: "陳大文", status: "待辦");
        await fixture.AddTodoAsync("乙的待辦", project.Id, owner: "王小明", status: "進行中");
        var service = fixture.CreateService();

        var summaries = await service.GetOwnerSummariesAsync(null);

        Assert.Equal(2, summaries.Count);
        var chen = summaries.Single(x => x.Owner == "陳大文");
        Assert.Equal(2, chen.Total);
        Assert.Equal(1, chen.Completed);
        Assert.Equal(1, chen.Pending);
        Assert.Equal(50, chen.CompletionPercent);

        var wang = summaries.Single(x => x.Owner == "王小明");
        Assert.Equal(1, wang.InProgress);
        Assert.Equal(0, wang.CompletionPercent);
    }

    [Fact]
    public async Task GetOwnerSummariesAsync_ShouldGroupNullAndBlankOwnerIntoUnassigned()
    {
        // null、空字串、全空白三種都要落進同一組，否則面板上會出現三個看起來一樣的「未指定」。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("沒有負責人", project.Id, owner: null);
        await fixture.AddTodoAsync("空字串負責人", project.Id, owner: string.Empty);
        await fixture.AddTodoAsync("全空白負責人", project.Id, owner: "   ");
        var service = fixture.CreateService();

        var summaries = await service.GetOwnerSummariesAsync(null);

        var unassigned = Assert.Single(summaries);
        Assert.Equal(TodoService.UnassignedOwner, unassigned.Owner);
        Assert.Equal(3, unassigned.Total);
    }

    [Fact]
    public async Task GetOwnerSummariesAsync_ShouldTrimOwnerWhenGrouping()
    {
        // Owner 是自由文字、沒有任何正規化，這是最可能出事的地方：
        // 交給 SQLite 的 GROUP BY 會把「陳大文」與「陳大文 」算成兩個人。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("待辦一", project.Id, owner: "陳大文");
        await fixture.AddTodoAsync("待辦二", project.Id, owner: "陳大文 ");
        await fixture.AddTodoAsync("待辦三", project.Id, owner: " 陳大文");
        var service = fixture.CreateService();

        var summaries = await service.GetOwnerSummariesAsync(null);

        var chen = Assert.Single(summaries);
        Assert.Equal("陳大文", chen.Owner);
        Assert.Equal(3, chen.Total);
    }

    [Fact]
    public async Task GetOwnerSummariesAsync_ShouldFilterByProject()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var projectA = await fixture.AddProjectAsync("專案A");
        var projectB = await fixture.AddProjectAsync("專案B");
        await fixture.AddTodoAsync("A 的待辦", projectA.Id, owner: "陳大文");
        await fixture.AddTodoAsync("B 的待辦", projectB.Id, owner: "王小明");
        var service = fixture.CreateService();

        var summaries = await service.GetOwnerSummariesAsync(projectA.Id);

        var only = Assert.Single(summaries);
        Assert.Equal("陳大文", only.Owner);
    }

    [Fact]
    public async Task GetOwnerSummariesAsync_ShouldCountOverdueOnlyForUnfinished()
    {
        // 與 TodoAdapterModel.IsOverdue 同語意：已完成的即使過了期限也不算逾期。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        var pastDue = DateTime.Today.AddDays(-3);
        await fixture.AddTodoAsync("過期未完成", project.Id, owner: "陳大文", status: "待辦", dueDate: pastDue);
        await fixture.AddTodoAsync("過期但已完成", project.Id, owner: "陳大文", status: "已完成", dueDate: pastDue);
        await fixture.AddTodoAsync("未到期", project.Id, owner: "陳大文", status: "待辦", dueDate: DateTime.Today.AddDays(3));
        var service = fixture.CreateService();

        var summaries = await service.GetOwnerSummariesAsync(null);

        Assert.Equal(1, Assert.Single(summaries).Overdue);
    }

    [Fact]
    public async Task GetByOwnerAsync_ShouldReturnUnassignedTodos_WhenOwnerIsUnassignedKey()
    {
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("沒有負責人", project.Id, owner: null);
        await fixture.AddTodoAsync("全空白負責人", project.Id, owner: "   ");
        await fixture.AddTodoAsync("有負責人", project.Id, owner: "陳大文");
        var service = fixture.CreateService();

        var items = await service.GetByOwnerAsync(TodoService.UnassignedOwner, null);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.True(string.IsNullOrWhiteSpace(item.Owner)));
    }

    [Fact]
    public async Task GetByOwnerAsync_ShouldMatchTrimmedOwner()
    {
        // 與分組鍵同一個規則：面板下拉給的是 Trim 過的名字，撈清單時要撈得到尾端有空白的資料。
        await using var fixture = await TodoServiceFixture.CreateAsync();
        var project = await fixture.AddProjectAsync("專案A");
        await fixture.AddTodoAsync("待辦一", project.Id, owner: "陳大文 ");
        var service = fixture.CreateService();

        var items = await service.GetByOwnerAsync("陳大文", null);

        Assert.Equal("待辦一", Assert.Single(items).Title);
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

        public TodoService CreateService()
        {
            return new TodoService(
                Context,
                mapper,
                loggerFactory.CreateLogger<TodoService>());
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
            string? owner = null,
            DateTime? dueDate = null)
        {
            var todo = new Todo
            {
                Title = title,
                ProjectId = projectId,
                MeetingId = meetingId,
                Status = status,
                Owner = owner,
                DueDate = dueDate,
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
