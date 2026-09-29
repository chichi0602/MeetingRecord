using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 專案權控（0.4.99）：管理者看全部；一般使用者只看得到自己是成員的專案與底下的待辦；
/// 負責人可以改專案、加減協作者；刪專案與轉移負責人只有管理者。
/// 會議那一半在 <c>MeetingServiceTests</c> 的「專案可見性」區塊。
/// </summary>
public sealed class ProjectAccessTests
{
    #region ProjectAccess 規則（純邏輯）

    [Fact]
    public void CanViewMeeting_UnassignedMeeting_ShouldOnlyBeVisibleToUploader()
    {
        var access = new ProjectAccess(false, 7, [1], []);

        Assert.True(access.CanViewMeeting(null, 7));
        Assert.False(access.CanViewMeeting(null, 8));
        // 沒有上傳者的舊會議：歸屬專案之前只有管理者看得到。
        Assert.False(access.CanViewMeeting(null, null));
        Assert.True(access.CanViewMeeting(1, null));
        Assert.False(access.CanViewMeeting(2, 7));
    }

    [Fact]
    public void UnresolvedUser_ShouldSeeNothing()
    {
        // 解析不到使用者（UserId=0）時，連上傳者為 null 的會議也不能被當成「自己的」。
        var access = new ProjectAccess(false, 0, [], []);

        Assert.False(access.CanViewMeeting(null, null));
        Assert.False(access.CanViewProject(1));
    }

    [Fact]
    public void Admin_ShouldPassEverything()
    {
        var access = new ProjectAccess(true, 1, [], []);

        Assert.True(access.CanViewProject(99));
        Assert.True(access.CanManageProject(99));
        Assert.True(access.CanViewMeeting(null, null));
    }

    #endregion

    #region 專案

    [Fact]
    public async Task ProjectList_ShouldOnlyContainMemberProjects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var mine = await fixture.AddProjectAsync("我的專案");
        await fixture.AddProjectAsync("別人的專案");
        await fixture.AddMemberAsync(mine.Id, alice.Id, ProjectMemberRole.Collaborator);

        var projects = await fixture.CreateProjectService(alice.Id).GetSelectableAsync();

        Assert.Equal(["我的專案"], projects.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task AddProject_CreatorShouldBecomeOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var service = fixture.CreateProjectService(alice.Id);

        var result = await service.AddAsync(new ProjectAdapterModel { Title = "新專案", Owner = "表單亂打的名字" });

        Assert.True(result.Success);
        var project = await fixture.Context.Project.AsNoTracking().SingleAsync();
        var member = await fixture.Context.ProjectMember.AsNoTracking().SingleAsync();
        Assert.Equal(alice.Id, member.MyUserId);
        Assert.Equal(ProjectMemberRole.Owner, member.Role);
        // 負責人姓名以帳號為準，不信任表單。
        Assert.Equal("alice", project.Owner);
    }

    [Fact]
    public async Task UpdateProject_CollaboratorShouldBeDenied_OwnerAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("owner");
        var collaborator = await fixture.AddUserAsync("collab");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.AddMemberAsync(project.Id, owner.Id, ProjectMemberRole.Owner);
        await fixture.AddMemberAsync(project.Id, collaborator.Id, ProjectMemberRole.Collaborator);

        var denied = await fixture.CreateProjectService(collaborator.Id)
            .UpdateAsync(new ProjectAdapterModel { Id = project.Id, Title = "協作者改的", Status = "進行中" });
        var allowed = await fixture.CreateProjectService(owner.Id)
            .UpdateAsync(new ProjectAdapterModel { Id = project.Id, Title = "負責人改的", Status = "進行中" });

        Assert.False(denied.Success);
        Assert.True(allowed.Success);
        Assert.Equal("負責人改的", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task DeleteProject_OwnerShouldBeDenied_AdminAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("owner");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.AddMemberAsync(project.Id, owner.Id, ProjectMemberRole.Owner);

        var denied = await fixture.CreateProjectService(owner.Id).DeleteAsync(project.Id);
        Assert.False(denied.Success);
        Assert.True(await fixture.Context.Project.AnyAsync());

        var allowed = await fixture.CreateProjectService(0, isAdmin: true).DeleteAsync(project.Id);
        Assert.True(allowed.Success);
        Assert.False(await fixture.Context.Project.AnyAsync());
    }

    #endregion

    #region 成員

    [Fact]
    public async Task Owner_CanAddAndRemoveCollaborators_ButNotRemoveOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("owner");
        var bob = await fixture.AddUserAsync("bob");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.AddMemberAsync(project.Id, owner.Id, ProjectMemberRole.Owner);
        var service = fixture.CreateMemberService(owner.Id);

        Assert.True((await service.AddCollaboratorAsync(project.Id, bob.Id)).Success);
        Assert.False((await service.RemoveCollaboratorAsync(project.Id, owner.Id)).Success);
        Assert.True((await service.RemoveCollaboratorAsync(project.Id, bob.Id)).Success);
        Assert.Equal([owner.Id], await fixture.Context.ProjectMember.Select(x => x.MyUserId).ToListAsync());
    }

    [Fact]
    public async Task Collaborator_CannotManageMembers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collaborator = await fixture.AddUserAsync("collab");
        var bob = await fixture.AddUserAsync("bob");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.AddMemberAsync(project.Id, collaborator.Id, ProjectMemberRole.Collaborator);

        var result = await fixture.CreateMemberService(collaborator.Id).AddCollaboratorAsync(project.Id, bob.Id);

        Assert.False(result.Success);
        Assert.Equal(1, await fixture.Context.ProjectMember.CountAsync());
    }

    [Fact]
    public async Task SetOwner_AdminOnly_DemotesPreviousOwnerAndSyncsName()
    {
        await using var fixture = await Fixture.CreateAsync();
        var oldOwner = await fixture.AddUserAsync("old");
        var newOwner = await fixture.AddUserAsync("new");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.AddMemberAsync(project.Id, oldOwner.Id, ProjectMemberRole.Owner);

        Assert.False((await fixture.CreateMemberService(oldOwner.Id).SetOwnerAsync(project.Id, newOwner.Id)).Success);
        Assert.True((await fixture.CreateMemberService(0, isAdmin: true).SetOwnerAsync(project.Id, newOwner.Id)).Success);

        var members = await fixture.Context.ProjectMember.AsNoTracking().ToDictionaryAsync(x => x.MyUserId, x => x.Role);
        Assert.Equal(ProjectMemberRole.Collaborator, members[oldOwner.Id]);
        Assert.Equal(ProjectMemberRole.Owner, members[newOwner.Id]);
        Assert.Equal("new", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Owner);
    }

    [Fact]
    public async Task SyncUserCollaborations_ShouldNotTouchOwnedProjects()
    {
        // 使用者管理頁把專案全部取消勾選，也不能把他負責的專案一起拔掉——負責人只能在專案頁轉移。
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var owned = await fixture.AddProjectAsync("她負責的");
        var joined = await fixture.AddProjectAsync("她協作的");
        var newOne = await fixture.AddProjectAsync("新加入的");
        await fixture.AddMemberAsync(owned.Id, alice.Id, ProjectMemberRole.Owner);
        await fixture.AddMemberAsync(joined.Id, alice.Id, ProjectMemberRole.Collaborator);

        var result = await fixture.CreateMemberService(0, isAdmin: true)
            .SyncUserCollaborationsAsync(alice.Id, [newOne.Id]);

        Assert.True(result.Success);
        var memberships = await fixture.Context.ProjectMember.AsNoTracking()
            .Where(x => x.MyUserId == alice.Id)
            .ToDictionaryAsync(x => x.ProjectId, x => x.Role);
        Assert.Equal(2, memberships.Count);
        Assert.Equal(ProjectMemberRole.Owner, memberships[owned.Id]);
        Assert.Equal(ProjectMemberRole.Collaborator, memberships[newOne.Id]);
    }

    #endregion

    #region 待辦

    [Fact]
    public async Task Todos_ShouldOnlyListMemberProjects_AndCannotMoveIntoOthers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var mine = await fixture.AddProjectAsync("我的專案");
        var others = await fixture.AddProjectAsync("別人的專案");
        await fixture.AddMemberAsync(mine.Id, alice.Id, ProjectMemberRole.Collaborator);
        var myTodo = await fixture.AddTodoAsync("我的待辦", mine.Id);
        await fixture.AddTodoAsync("別人的待辦", others.Id);
        var service = fixture.CreateTodoService(alice.Id);

        var list = await service.GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50 });
        Assert.Equal(["我的待辦"], list.Result.Select(x => x.Title).ToList());

        var model = await service.GetAsync(myTodo.Id);
        model.ProjectId = others.Id;
        var moved = await service.UpdateAsync(model);

        Assert.False(moved.Success);
        Assert.Equal(mine.Id, (await fixture.Context.Todo.AsNoTracking().SingleAsync(x => x.Id == myTodo.Id)).ProjectId);
    }

    #endregion

    #region 舊資料回填

    [Fact]
    public async Task Backfill_ShouldAssignOwnerByUniqueNameOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice", name: "王小明");
        await fixture.AddUserAsync("twin1", name: "陳大文");
        await fixture.AddUserAsync("twin2", name: "陳大文");
        var matched = await fixture.AddProjectAsync("對得上", owner: "王小明");
        var ambiguous = await fixture.AddProjectAsync("同名兩個", owner: "陳大文");
        var unknown = await fixture.AddProjectAsync("對不上", owner: "查無此人");

        await fixture.CreateBackfillService().RunAsync();
        await fixture.CreateBackfillService().RunAsync();

        var members = await fixture.Context.ProjectMember.AsNoTracking().ToListAsync();
        var member = Assert.Single(members);
        Assert.Equal(matched.Id, member.ProjectId);
        Assert.Equal(alice.Id, member.MyUserId);
        Assert.Equal(ProjectMemberRole.Owner, member.Role);
        Assert.DoesNotContain(members, x => x.ProjectId == ambiguous.Id || x.ProjectId == unknown.Id);
    }

    [Fact]
    public async Task Backfill_ShouldTakeUploaderFromFirstTranscriptionInLedger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var meeting = new Meeting { Title = "舊會議" };
        fixture.Context.Meeting.Add(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.AiUsageLog.AddRange(
            new AiUsageLog { Feature = AiUsageFeature.Transcription, MeetingId = meeting.Id, UserId = bob.Id, OccurredAt = new DateTime(2026, 9, 20) },
            new AiUsageLog { Feature = AiUsageFeature.Transcription, MeetingId = meeting.Id, UserId = alice.Id, OccurredAt = new DateTime(2026, 9, 1) });
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateBackfillService().RunAsync();

        // 最早那一筆才是當初上傳觸發的轉錄；之後的是別人按了重新轉錄。
        Assert.Equal(alice.Id, (await fixture.Context.Meeting.AsNoTracking().SingleAsync()).CreatedByUserId);
    }

    #endregion

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly IMapper mapper;
        private readonly IOptions<SystemSettings> systemSettings;
        private readonly string rootPath;

        private Fixture(SqliteConnection connection, BackendDBContext context, string rootPath)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();

            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = Path.Combine(rootPath, "projectfile");
            settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "aichat");
            systemSettings = Options.Create(settings);
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(
                new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new Fixture(connection, context, rootPath);
        }

        private ProjectAccessService Access(int userId, bool isAdmin)
            => isAdmin ? TestProjectAccess.Admin(Context, userId) : TestProjectAccess.User(Context, userId);

        public ProjectService CreateProjectService(int userId, bool isAdmin = false)
            => new(
                Context,
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                systemSettings,
                new AiChatStore(systemSettings, loggerFactory.CreateLogger<AiChatStore>()),
                Access(userId, isAdmin));

        public ProjectMemberService CreateMemberService(int userId, bool isAdmin = false)
            => new(Context, Access(userId, isAdmin), loggerFactory.CreateLogger<ProjectMemberService>());

        public TodoService CreateTodoService(int userId, bool isAdmin = false)
            => new(Context, mapper, loggerFactory.CreateLogger<TodoService>(), Access(userId, isAdmin));

        public ProjectAccessBackfillService CreateBackfillService()
            => new(Context, loggerFactory.CreateLogger<ProjectAccessBackfillService>());

        public async Task<MyUser> AddUserAsync(string account, string? name = null)
        {
            var user = new MyUser { Account = account, Name = name ?? account, Password = "x", Status = true };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<Project> AddProjectAsync(string title, string owner = "")
        {
            var project = new Project { Title = title, Status = "進行中", Owner = owner };
            Context.Project.Add(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return project;
        }

        public async Task AddMemberAsync(int projectId, int userId, ProjectMemberRole role)
        {
            Context.ProjectMember.Add(new ProjectMember { ProjectId = projectId, MyUserId = userId, Role = role });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task<Todo> AddTodoAsync(string title, int projectId)
        {
            var todo = new Todo { Title = title, ProjectId = projectId, Status = "待辦", Priority = "中" };
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
