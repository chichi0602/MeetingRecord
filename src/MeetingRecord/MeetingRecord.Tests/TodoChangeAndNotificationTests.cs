using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Repositories;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 待辦異動紀錄與站內通知（1.0.44）。使用者問「有人改了期限，一起負責那項專案的人要怎麼知道」。
///
/// <para>
/// 情境：專案一的主責團隊有 A（操作者）、B、D（已停用）；協作團隊有 E；專案二的團隊只有 C。
/// </para>
/// </summary>
public sealed class TodoChangeAndNotificationTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly IMapper mapper;
    private readonly RecordingNotificationNotifier notifier = new();

    private int userA, userB, userC, userD, userE;
    private int project1, project2;

    public TodoChangeAndNotificationTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
        Seed();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ───────── 異動紀錄與通知 ─────────

    [Fact]
    public async Task UpdateAsync_DueDateChanged_ShouldRecordAndNotifyProjectMembersExceptActor()
    {
        var todo = await AddTodoAsync("完成報表", project1, dueDate: new DateTime(2026, 10, 20));
        var model = await CreateService(userA).GetAsync(todo.Id);
        model.DueDate = new DateTime(2026, 10, 31);

        var result = await CreateService(userA).UpdateAsync(model);

        Assert.True(result.Success);
        var change = await context.TodoChange.AsNoTracking().SingleAsync();
        Assert.Equal(TodoChangeFields.DueDate, change.Field);
        Assert.Equal("2026/10/20", change.OldValue);
        Assert.Equal("2026/10/31", change.NewValue);
        Assert.Equal("甲", change.ChangedByName);
        Assert.Equal(AuditVia.Web, change.Via);

        // B 收到；A 是改的人不收；D 停用不收；C 不是這個專案的人不收；E 在協作團隊要收。
        var recipients = await context.UserNotification.AsNoTracking().Select(x => x.UserId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { userB, userE }.Order(), recipients);
        Assert.Contains("甲 把『完成報表』的期限改為 2026/10/31", (await context.UserNotification.AsNoTracking().FirstAsync()).Message);
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public async Task UpdateAsync_TitleAndPriorityOnly_ShouldRecordButNotNotify()
    {
        // 標題、優先度常常是順手修字，只記異動不通知。
        var todo = await AddTodoAsync("完成報表", project1);
        var model = await CreateService(userA).GetAsync(todo.Id);
        model.Title = "完成統計報表";
        model.Priority = "高";

        await CreateService(userA).UpdateAsync(model);

        var fields = await context.TodoChange.AsNoTracking().Select(x => x.Field).ToListAsync();
        Assert.Equal(new[] { TodoChangeFields.Priority, TodoChangeFields.Title }.Order(), fields.Order());
        Assert.False(await context.UserNotification.AnyAsync());
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task UpdateAsync_NothingChanged_ShouldWriteNothing()
    {
        var todo = await AddTodoAsync("完成報表", project1);
        var model = await CreateService(userA).GetAsync(todo.Id);

        await CreateService(userA).UpdateAsync(model);

        Assert.False(await context.TodoChange.AnyAsync());
        Assert.False(await context.UserNotification.AnyAsync());
    }

    [Fact]
    public async Task SetCompletedAsync_ShouldRecordStatusChangeAndNotify()
    {
        var todo = await AddTodoAsync("完成報表", project1);

        await CreateService(userA).SetCompletedAsync(todo.Id, true);

        var change = await context.TodoChange.AsNoTracking().SingleAsync();
        Assert.Equal(TodoChangeFields.Status, change.Field);
        Assert.Equal("待辦", change.OldValue);
        Assert.Equal(TodoAdapterModel.CompletedStatus, change.NewValue);
        Assert.True(await context.UserNotification.AnyAsync(x => x.UserId == userB));
    }

    [Fact]
    public async Task UpdateAsync_MovedToAnotherProject_ShouldNotifyBothProjects()
    {
        // A 是管理者才搬得動兩個專案；兩邊的人都要知道（狀態一起改，才會觸發通知）。
        var todo = await AddTodoAsync("完成報表", project1);
        var model = await CreateService(userA, isAdmin: true).GetAsync(todo.Id);
        model.ProjectId = project2;
        model.Status = "進行中";

        await CreateService(userA, isAdmin: true).UpdateAsync(model);

        var recipients = await context.UserNotification.AsNoTracking().Select(x => x.UserId).ToListAsync();
        Assert.Contains(userB, recipients);
        Assert.Contains(userC, recipients);
    }

    [Fact]
    public async Task ApplyExtractedUpdateAsync_ShouldOnlyUpdateDueDateAndOwner_AndRecordSourceMeeting()
    {
        var todo = await AddTodoAsync("完成報表", project1, owner: "王小明", dueDate: new DateTime(2026, 10, 20));

        var result = await CreateService(userA).ApplyExtractedUpdateAsync(todo.Id, new DateTime(2026, 10, 31), "林大華", 99, "10/16 週會");

        Assert.True(result.Success);
        var saved = await context.Todo.AsNoTracking().SingleAsync(x => x.Id == todo.Id);
        Assert.Equal(new DateTime(2026, 10, 31), saved.DueDate);
        Assert.Equal("林大華", saved.Owner);
        Assert.Equal("完成報表", saved.Title);
        Assert.Equal("待辦", saved.Status);

        var changes = await context.TodoChange.AsNoTracking().ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, x =>
        {
            Assert.Equal(AuditVia.AiExtract, x.Via);
            Assert.Equal(99, x.SourceMeetingId);
            Assert.Equal("10/16 週會", x.SourceMeetingTitle);
        });

        var message = (await context.UserNotification.AsNoTracking().FirstAsync()).Message;
        Assert.Contains("期限改為 2026/10/31、負責人改為 林大華", message);
        Assert.Contains("從〈10/16 週會〉抽出", message);
    }

    [Fact]
    public async Task ApplyExtractedUpdateAsync_MissingValues_ShouldNotClearExisting()
    {
        // 這場會議沒講期限（null）時，不能把既有期限清掉。
        var todo = await AddTodoAsync("完成報表", project1, owner: "王小明", dueDate: new DateTime(2026, 10, 20));

        var result = await CreateService(userA).ApplyExtractedUpdateAsync(todo.Id, null, "  ", 99, "週會");

        Assert.True(result.Success);
        var saved = await context.Todo.AsNoTracking().SingleAsync(x => x.Id == todo.Id);
        Assert.Equal(new DateTime(2026, 10, 20), saved.DueDate);
        Assert.Equal("王小明", saved.Owner);
        Assert.False(await context.TodoChange.AnyAsync());
    }

    [Fact]
    public async Task GetAsync_List_ShouldFlagRecentDueDateChangesOnly()
    {
        var recent = await AddTodoAsync("最近改過", project1, dueDate: new DateTime(2026, 10, 20));
        var old = await AddTodoAsync("很久以前改過", project1, dueDate: new DateTime(2026, 10, 20));
        context.TodoChange.AddRange(
            new TodoChange { TodoId = recent.Id, ProjectId = project1, Field = TodoChangeFields.DueDate, OldValue = "2026/10/10", NewValue = "2026/10/20", ChangedByName = "甲", ChangedAt = DateTime.Now.AddDays(-1), Via = AuditVia.Web },
            new TodoChange { TodoId = old.Id, ProjectId = project1, Field = TodoChangeFields.DueDate, OldValue = "2026/09/10", NewValue = "2026/10/20", ChangedByName = "甲", ChangedAt = DateTime.Now.AddDays(-TodoService.DueDateChangedBadgeDays - 1), Via = AuditVia.Web });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var list = (await CreateService(userA).GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50 })).Result.ToList();

        var flagged = list.Single(x => x.Id == recent.Id);
        Assert.True(flagged.HasRecentDueDateChange);
        Assert.Contains("原 2026/10/10", flagged.DueDateChangeText);
        Assert.False(list.Single(x => x.Id == old.Id).HasRecentDueDateChange);
    }

    [Fact]
    public async Task GetChangesAsync_NonMember_ShouldSeeNothing()
    {
        var todo = await AddTodoAsync("完成報表", project1, dueDate: new DateTime(2026, 10, 20));
        await CreateService(userA).ApplyExtractedUpdateAsync(todo.Id, new DateTime(2026, 10, 31), null, 1, "週會");

        Assert.Single(await CreateService(userB).GetChangesAsync(todo.Id));
        Assert.Empty(await CreateService(userC).GetChangesAsync(todo.Id));
    }

    [Fact]
    public async Task DeleteTodo_ShouldCascadeChanges()
    {
        var todo = await AddTodoAsync("完成報表", project1, dueDate: new DateTime(2026, 10, 20));
        await CreateService(userA).ApplyExtractedUpdateAsync(todo.Id, new DateTime(2026, 10, 31), null, 1, "週會");

        var result = await CreateService(userA).DeleteAsync(todo.Id);

        Assert.True(result.Success);
        Assert.False(await context.TodoChange.AnyAsync());
    }

    [Fact]
    public async Task ApiRepository_Update_ShouldRecordViaApi()
    {
        // 改待辦的每一條路徑都要經過記錄器——API 改的也要讓專案成員知道。
        var todo = await AddTodoAsync("完成報表", project1, dueDate: new DateTime(2026, 10, 20));
        var access = TestProjectAccess.User(context, userA);
        var actor = TestAuditActor.Fixed(userA, "a");
        var repository = new TodoRepository(
            context,
            access,
            TestAuditLogs.CreateService(context, actor),
            TestTodoChanges.CreateRecorder(context, access, actor, notifier));

        var update = await context.Todo.AsNoTracking().SingleAsync(x => x.Id == todo.Id);
        update.DueDate = new DateTime(2026, 11, 5);
        context.ChangeTracker.Clear();

        Assert.True(await repository.UpdateAsync(update));
        var change = await context.TodoChange.AsNoTracking().SingleAsync();
        Assert.Equal(AuditVia.Api, change.Via);
        Assert.Equal("2026/11/05", change.NewValue);
        Assert.True(await context.UserNotification.AnyAsync(x => x.UserId == userB));
    }

    // ───────── 通知服務 ─────────

    [Fact]
    public async Task NotificationService_ShouldOnlyTouchOwnNotifications()
    {
        context.UserNotification.AddRange(
            new UserNotification { UserId = userB, Message = "B1", CreatedAt = DateTime.Now.AddMinutes(-2) },
            new UserNotification { UserId = userB, Message = "B2", CreatedAt = DateTime.Now.AddMinutes(-1) },
            new UserNotification { UserId = userC, Message = "C1", CreatedAt = DateTime.Now });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new UserNotificationService(context, notifier);
        var cNotification = await context.UserNotification.AsNoTracking().SingleAsync(x => x.UserId == userC);

        Assert.Equal(2, await service.GetUnreadCountAsync(userB));
        Assert.Equal(["B2", "B1"], (await service.GetRecentAsync(userB)).Select(x => x.Message));

        // B 不能把 C 的通知標成已讀。
        await service.MarkReadAsync(userB, cNotification.Id);
        Assert.Equal(1, await service.GetUnreadCountAsync(userC));

        await service.MarkAllReadAsync(userB);
        Assert.Equal(0, await service.GetUnreadCountAsync(userB));
        Assert.Equal(1, await service.GetUnreadCountAsync(userC));
    }

    [Fact]
    public async Task NotificationService_PurgeBefore_ShouldRemoveOldOnly()
    {
        context.UserNotification.AddRange(
            new UserNotification { UserId = userB, Message = "舊", CreatedAt = DateTime.Now.AddDays(-100) },
            new UserNotification { UserId = userB, Message = "新", CreatedAt = DateTime.Now });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var removed = await new UserNotificationService(context, notifier).PurgeBeforeAsync(DateTime.Now.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Equal("新", (await context.UserNotification.AsNoTracking().SingleAsync()).Message);
    }

    [Fact]
    public async Task DeleteUser_ShouldCascadeNotifications()
    {
        context.UserNotification.Add(new UserNotification { UserId = userD, Message = "x", CreatedAt = DateTime.Now });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        context.MyUser.Remove(await context.MyUser.SingleAsync(x => x.Id == userD));
        await context.SaveChangesAsync();

        Assert.False(await context.UserNotification.AnyAsync());
    }

    // ───────── 純函式 ─────────

    [Fact]
    public void BuildMessage_ShouldNotMentionSourceForManualEdits()
    {
        var message = TodoChangeRecorder.BuildMessage("甲", "完成報表", [(TodoChangeFields.Owner, "王小明", null)], AuditVia.Web, null);

        Assert.Equal("甲 把『完成報表』的負責人改為 未指定", message);
    }

    // ───────── 測試資料 ─────────

    private TodoService CreateService(int userId, bool isAdmin = false)
    {
        var access = isAdmin ? TestProjectAccess.Admin(context, userId) : TestProjectAccess.User(context, userId);
        var actor = TestAuditActor.Fixed(userId, $"user{userId}");
        return new TodoService(
            context,
            mapper,
            NullLogger<TodoService>.Instance,
            access,
            TestAuditLogs.CreateService(context, actor),
            TestTodoChanges.CreateRecorder(context, access, actor, notifier));
    }

    private async Task<Todo> AddTodoAsync(string title, int projectId, string? owner = null, DateTime? dueDate = null)
    {
        var todo = new Todo { Title = title, ProjectId = projectId, Owner = owner, DueDate = dueDate, Status = "待辦", Priority = "中" };
        context.Todo.Add(todo);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return todo;
    }

    private void Seed()
    {
        MyUser User(string account, string name, bool active = true) => new() { Account = account, Name = name, Password = "x", Status = active };

        var a = User("a", "甲");
        var b = User("b", "乙");
        var c = User("c", "丙");
        var d = User("d", "丁", active: false);
        var e = User("e", "戊");
        context.MyUser.AddRange(a, b, c, d, e);

        var primary = new Team { Name = "主責團隊" };
        var collaborator = new Team { Name = "協作團隊" };
        var other = new Team { Name = "別的團隊" };
        context.Team.AddRange(primary, collaborator, other);

        var p1 = new Project { Title = "專案一" };
        var p2 = new Project { Title = "專案二" };
        context.Project.AddRange(p1, p2);
        context.SaveChanges();

        context.UserTeam.AddRange(
            new UserTeam { MyUserId = a.Id, TeamId = primary.Id },
            new UserTeam { MyUserId = b.Id, TeamId = primary.Id },
            new UserTeam { MyUserId = d.Id, TeamId = primary.Id },
            new UserTeam { MyUserId = e.Id, TeamId = collaborator.Id },
            new UserTeam { MyUserId = c.Id, TeamId = other.Id });
        context.ProjectTeam.AddRange(
            new ProjectTeam { ProjectId = p1.Id, TeamId = primary.Id, IsPrimary = true },
            new ProjectTeam { ProjectId = p1.Id, TeamId = collaborator.Id, IsPrimary = false },
            new ProjectTeam { ProjectId = p2.Id, TeamId = other.Id, IsPrimary = true });
        context.SaveChanges();
        context.ChangeTracker.Clear();

        (userA, userB, userC, userD, userE) = (a.Id, b.Id, c.Id, d.Id, e.Id);
        (project1, project2) = (p1.Id, p2.Id);
    }
}
