using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

public sealed class EffectiveTeamResolverTests
{
    [Fact]
    public async Task ShouldReturnRoleDefaultTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("R", new[] { "團隊A" });
        var user = await fixture.AddUserAsync("u", role.Id);
        var resolver = new EffectiveTeamResolver(fixture.Context);

        var teams = await resolver.GetEffectiveTeamNamesAsync(user.Id);

        Assert.Contains("團隊A", teams);
    }

    [Fact]
    public async Task ShouldReturnDirectUserTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("R", Array.Empty<string>());
        var user = await fixture.AddUserAsync("u", role.Id);
        var team = await fixture.AddTeamAsync("團隊B");
        await fixture.AddUserTeamAsync(user.Id, team.Id);
        var resolver = new EffectiveTeamResolver(fixture.Context);

        var teams = await resolver.GetEffectiveTeamNamesAsync(user.Id);

        Assert.Contains("團隊B", teams);
    }

    [Fact]
    public async Task ShouldUnionAndDeduplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("R", new[] { "團隊A", "團隊C" });
        var user = await fixture.AddUserAsync("u", role.Id);
        var teamA = await fixture.AddTeamAsync("團隊A"); // 與角色團隊重複
        var teamB = await fixture.AddTeamAsync("團隊B");
        await fixture.AddUserTeamAsync(user.Id, teamA.Id);
        await fixture.AddUserTeamAsync(user.Id, teamB.Id);
        var resolver = new EffectiveTeamResolver(fixture.Context);

        var teams = await resolver.GetEffectiveTeamNamesAsync(user.Id);

        Assert.Equal(3, teams.Count);
        Assert.Contains("團隊A", teams);
        Assert.Contains("團隊B", teams);
        Assert.Contains("團隊C", teams);
    }

    [Fact]
    public async Task ShouldReturnEmptyForUnknownUser()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolver = new EffectiveTeamResolver(fixture.Context);

        var teams = await resolver.GetEffectiveTeamNamesAsync(999);

        Assert.Empty(teams);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public async Task<RoleView> AddRoleAsync(string name, string[] defaultTeams)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]", DefaultTeamsJson = JsonSerializer.Serialize(defaultTeams) };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<MyUser> AddUserAsync(string account, int roleViewId)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = true, RoleViewId = roleViewId };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<Team> AddTeamAsync(string name)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async Task AddUserTeamAsync(int userId, int teamId)
        {
            Context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = teamId });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
