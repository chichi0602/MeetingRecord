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
        fixture.Context.RoleView.Add(role);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        var service = fixture.CreateRoleService();

        var result = await service.DeleteAsync(role.Id);

        Assert.True(result.Success);
        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync(x => x.Action == "Role.Delete");
        Assert.Equal(role.Id.ToString(), audit.TargetId);
        Assert.Contains("name=臨時角色", audit.Detail);
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

        private AuditLogService Audit() => new(Context, loggerFactory.CreateLogger<AuditLogService>());

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
