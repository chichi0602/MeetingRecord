using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>驗證使用者/角色異動會寫入 AuditLog（誰、做了什麼、對什麼）。</summary>
public sealed class AuditEventsTests
{
    [Fact]
    public async Task UserAdd_ShouldWriteUserCreateAudit()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();

        var result = await service.AddAsync(new MyUserAdapterModel
        {
            Account = "newbie",
            Name = "newbie",
            Password = "pw",
            Status = true,
        });

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "User.Create");
        Assert.True(audit.Success);
        Assert.Equal(7, audit.ActorUserId);
        Assert.Equal("actor7", audit.ActorAccount);
        Assert.Equal(nameof(MyUser), audit.TargetType);
        Assert.Contains("account=newbie", audit.Detail);
    }

    [Fact]
    public async Task UserUpdate_ShouldWriteUserUpdateAudit()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        await service.AddAsync(new MyUserAdapterModel { Account = "u", Name = "u", Password = "pw", Status = true });
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "u");

        var result = await service.UpdateAsync(new MyUserAdapterModel
        {
            Id = user.Id,
            Account = "u",
            Name = "u-renamed",
            Status = true,
        });

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "User.Update");
        Assert.Equal(user.Id.ToString(), audit.TargetId);
        Assert.Equal("actor7", audit.ActorAccount);
    }

    [Fact]
    public async Task UserDelete_ShouldWriteUserDeleteAuditWithAccount()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        await service.AddAsync(new MyUserAdapterModel { Account = "gone", Name = "gone", Password = "pw", Status = true });
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "gone");

        var result = await service.DeleteAsync(user.Id);

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "User.Delete");
        Assert.Equal(user.Id.ToString(), audit.TargetId);
        Assert.Contains("account=gone", audit.Detail);
    }

    [Fact]
    public async Task RoleAdd_ShouldWriteRoleCreateAudit()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateRoleService();

        var result = await service.AddAsync(new RoleViewAdapterModel
        {
            Name = "稽核員",
            RolePermission = new RolePermissionService().InitializePermissionSetting(),
        });

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "Role.Create");
        Assert.True(audit.Success);
        Assert.Equal("actor7", audit.ActorAccount);
        Assert.Equal(nameof(RoleView), audit.TargetType);
        Assert.Contains("name=稽核員", audit.Detail);
    }

    [Fact]
    public async Task RoleDelete_ShouldWriteRoleDeleteAudit()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var role = new RoleView { Name = "臨時角色", TabViewJson = "[]" };
        // 0.4.110 起不能刪最後一個角色，另放一個讓它不是最後一個。
        fixture.Context.RoleView.AddRange(role, new RoleView { Name = "保留角色", TabViewJson = "[]" });
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        var service = fixture.CreateRoleService();

        var result = await service.DeleteAsync(role.Id);

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "Role.Delete");
        Assert.Equal(role.Id.ToString(), audit.TargetId);
        Assert.Contains("name=臨時角色", audit.Detail);
    }

    // ───────── 1.0.33：密碼、啟停用、團隊成員 ─────────

    [Fact]
    public async Task UserUpdate_WithNewPassword_ShouldAlsoWritePasswordReset()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "reset-me");

        await service.UpdateAsync(new MyUserAdapterModel { Id = user.Id, Account = "reset-me", Name = "reset-me", Password = "new-pw", Status = true });

        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PasswordReset);
        Assert.Equal(7, audit.ActorUserId);
        Assert.Equal(user.Id.ToString(), audit.TargetId);
        Assert.Contains("by=admin", audit.Detail);
        Assert.DoesNotContain("new-pw", audit.Detail);
    }

    [Fact]
    public async Task UserUpdate_WithoutPassword_ShouldNotWritePasswordReset()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "keep-pw");

        await service.UpdateAsync(new MyUserAdapterModel { Id = user.Id, Account = "keep-pw", Name = "renamed", Status = true });

        Assert.False(await fixture.Context.AuditLog.AnyAsync(x => x.Action == AuditActions.PasswordReset));
    }

    /// <summary>首次登入改密碼：操作者與目標都是本人（目前使用者還沒載入時退回用本人）。</summary>
    [Fact]
    public async Task ChangeOwnPassword_ShouldWritePasswordChange_ForSuccessAndWrongCurrentPassword()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 0, actorAccount: "");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "self");

        await service.ChangeOwnPasswordAsync(user.Id, "wrong", "new-pw", "new-pw");
        var ok = await service.ChangeOwnPasswordAsync(user.Id, "pw", "new-pw", "new-pw");

        Assert.True(ok.Success);
        var audits = await fixture.Context.AuditLog.AsNoTracking().Where(x => x.Action == AuditActions.PasswordChange).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.False(audits[0].Success);
        Assert.Contains("reason=WrongCurrentPassword", audits[0].Detail);
        Assert.True(audits[1].Success);
        Assert.Equal(user.Id, audits[1].ActorUserId);
        Assert.Equal("self", audits[1].ActorAccount);
        Assert.Equal("by=self; via=FirstLogin", audits[1].Detail);
    }

    [Fact]
    public async Task ChangePassword_FromDialog_ShouldWritePasswordChangeViaDialog()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 0, actorAccount: "");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "dialog");

        var result = await service.ChangePasswordAsync(user.Id, "pw", "new-pw");

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PasswordChange);
        Assert.Equal("by=self; via=Dialog", audit.Detail);
    }

    [Fact]
    public async Task SetOwnApiPassword_ShouldWritePasswordApiSet()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 0, actorAccount: "");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "api-user");

        var result = await service.SetOwnApiPasswordAsync(user.Id, "pw", "api-pw", "api-pw");

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PasswordApiSet);
        Assert.True(audit.Success);
        Assert.Equal(user.Id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task SetStatus_ShouldWriteUserDisableThenEnable()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        var user = await fixture.AddUserAsync(service, "toggle");

        await service.SetStatusAsync(user.Id, false);
        await service.SetStatusAsync(user.Id, true);

        var actions = await fixture.Context.AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.UserDisable || x.Action == AuditActions.UserEnable)
            .OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
        Assert.Equal([AuditActions.UserDisable, AuditActions.UserEnable], actions);
    }

    [Fact]
    public async Task TeamMembership_ShouldWriteAddAndRemove_AndSkipWhenNothingChanged()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 7, actorAccount: "actor7");
        var service = fixture.CreateUserService();
        var a = await fixture.AddUserAsync(service, "member-a");
        var b = await fixture.AddUserAsync(service, "member-b");
        var team = new Team { Name = "研發" };
        fixture.Context.Team.Add(team);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await service.AddUsersToTeamAsync([a.Id, b.Id], team.Id);
        await service.AddUsersToTeamAsync([a.Id], team.Id);
        await service.RemoveUsersFromTeamAsync([b.Id], team.Id);

        var audits = await fixture.Context.AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.UserTeamAdd || x.Action == AuditActions.UserTeamRemove)
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(AuditActions.UserTeamAdd, audits[0].Action);
        Assert.Equal(nameof(Team), audits[0].TargetType);
        Assert.Equal(team.Id.ToString(), audits[0].TargetId);
        Assert.Equal($"count=2; userIds={a.Id},{b.Id}", audits[0].Detail);
        Assert.Equal(AuditActions.UserTeamRemove, audits[1].Action);
        Assert.Equal($"count=1; userIds={b.Id}", audits[1].Detail);
    }

    [Fact]
    public async Task Mutation_WithoutAuthenticatedActor_ShouldWriteNullActorUserId()
    {
        await using var fixture = await Fixture.CreateAsync(actorId: 0, actorAccount: "");
        var service = fixture.CreateUserService();

        await service.AddAsync(new MyUserAdapterModel { Account = "anon", Name = "anon", Password = "pw", Status = true });

        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "User.Create");
        Assert.Null(audit.ActorUserId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;
        private readonly CurrentUserService currentUserService;

        private Fixture(SqliteConnection connection, BackendDBContext context, int actorId, string actorAccount)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
            currentUserService = new CurrentUserService();
            currentUserService.CurrentUser.Id = actorId;
            currentUserService.CurrentUser.Account = actorAccount;
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync(int actorId, string actorAccount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context, actorId, actorAccount);
        }

        private AuditLogService Audit() => TestAuditLogs.CreateService(Context);

        /// <summary>新增一個密碼為 <c>pw</c> 的使用者並回傳資料庫裡的那一列。</summary>
        public async Task<MyUser> AddUserAsync(MyUserService service, string account)
        {
            var result = await service.AddAsync(new MyUserAdapterModel { Account = account, Name = account, Password = "pw", Status = true });
            Assert.True(result.Success, result.Message);
            return await Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == account);
        }

        public MyUserService CreateUserService()
            => new(Context, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context), Audit(), currentUserService);

        public RoleViewService CreateRoleService()
            => new(Context, mapper, loggerFactory.CreateLogger<RoleViewService>(), new RolePermissionService(), new RbacWriteService(Context), Audit(), currentUserService);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
