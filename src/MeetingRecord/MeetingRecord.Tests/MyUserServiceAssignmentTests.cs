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

public sealed class MyUserServiceAssignmentTests
{
    [Fact]
    public async Task AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam()
    {
        await using var fixture = await MyUserServiceAssignmentFixture.CreateAsync();
        var roleA = await fixture.AddRoleAsync("角色A");
        var roleB = await fixture.AddRoleAsync("角色B");
        var teamA = await fixture.AddTeamAsync("團隊A");
        var service = fixture.CreateService();

        var result = await service.AddAsync(new MyUserAdapterModel
        {
            Account = "multi",
            Name = "multi",
            Password = "pw",
            Status = true,
            RoleViewId = roleA.Id,
            AdditionalRoleIds = new List<int> { roleB.Id },
            TeamNames = new List<string> { "團隊A" },
        });

        Assert.True(result.Success);
        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Account == "multi");

        var roleIds = await fixture.Context.UserRole.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.RoleViewId).ToListAsync();
        Assert.Contains(roleA.Id, roleIds);
        Assert.Contains(roleB.Id, roleIds);

        var teamIds = await fixture.Context.UserTeam.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.TeamId).ToListAsync();
        Assert.Contains(teamA.Id, teamIds);

        var (additionalRoleIds, teamNames) = await service.GetUserAssignmentsAsync(user.Id);
        Assert.Contains(roleB.Id, additionalRoleIds);
        Assert.DoesNotContain(roleA.Id, additionalRoleIds); // 主要角色不列入額外角色
        Assert.Contains("團隊A", teamNames);
    }

    private sealed class MyUserServiceAssignmentFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private MyUserServiceAssignmentFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<MyUserServiceAssignmentFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new MyUserServiceAssignmentFixture(connection, context);
        }

        public MyUserService CreateService()
            => new(Context, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context),
                new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>()), new CurrentUserService());

        public async Task<RoleView> AddRoleAsync(string name)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<Team> AddTeamAsync(string name)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
