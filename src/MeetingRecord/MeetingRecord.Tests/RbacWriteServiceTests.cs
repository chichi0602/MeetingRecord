using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

public sealed class RbacWriteServiceTests
{
    [Fact]
    public async Task SyncRolePermissionsAsync_ShouldAddAndRemoveToMatchKeys()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("R1");
        var service = new RbacWriteService(fixture.Context);

        await service.SyncRolePermissionsAsync(role.Id, new[] { "會議紀錄", "專案項目" });
        await service.SyncRolePermissionsAsync(role.Id, new[] { "會議紀錄", "分類清單" }); // 移除專案項目、加入分類清單

        var keys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();

        Assert.Equal(2, keys.Count);
        Assert.Contains("會議紀錄", keys);
        Assert.Contains("分類清單", keys);
        Assert.DoesNotContain("專案項目", keys);
    }

    [Fact]
    public async Task SyncRolePermissionsAsync_ShouldCreateMissingPermissionRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("R1");
        var service = new RbacWriteService(fixture.Context);

        await service.SyncRolePermissionsAsync(role.Id, new[] { "全新權限鍵" });

        Assert.Equal(1, await fixture.Context.Permission.CountAsync(x => x.Key == "全新權限鍵"));
    }

    [Fact]
    public async Task SyncUserRolesAsync_ShouldReconcile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("u1");
        var r1 = await fixture.AddRoleAsync("R1");
        var r2 = await fixture.AddRoleAsync("R2");
        var service = new RbacWriteService(fixture.Context);

        await service.SyncUserRolesAsync(user.Id, new[] { r1.Id, r2.Id });
        await service.SyncUserRolesAsync(user.Id, new[] { r2.Id }); // 移除 r1

        var roleIds = await fixture.Context.UserRole.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.RoleViewId).ToListAsync();

        Assert.Single(roleIds);
        Assert.Contains(r2.Id, roleIds);
    }

    [Fact]
    public async Task SyncUserTeamsAsync_ShouldReconcile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("u1");
        var t1 = await fixture.AddTeamAsync("T1");
        var t2 = await fixture.AddTeamAsync("T2");
        var service = new RbacWriteService(fixture.Context);

        await service.SyncUserTeamsAsync(user.Id, new[] { t1.Id });
        await service.SyncUserTeamsAsync(user.Id, new[] { t1.Id, t2.Id });

        var teamIds = await fixture.Context.UserTeam.AsNoTracking()
            .Where(x => x.MyUserId == user.Id).Select(x => x.TeamId).ToListAsync();

        Assert.Equal(2, teamIds.Count);
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

        public async Task<RoleView> AddRoleAsync(string name)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<MyUser> AddUserAsync(string account)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = true };
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

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
