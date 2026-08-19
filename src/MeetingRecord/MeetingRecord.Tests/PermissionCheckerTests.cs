using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

public sealed class PermissionCheckerTests
{
    [Fact]
    public async Task HasPermissionAsync_ForAdmin_ShouldReturnTrueForAnyKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("root", isAdmin: true, permissions: Array.Empty<string>());
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "任何權限"));
    }

    [Fact]
    public async Task HasPermissionAsync_WhenRoleHasKey_ShouldReturnTrue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", isAdmin: false, permissions: new[] { "專案項目", "首頁" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目"));
    }

    [Fact]
    public async Task HasPermissionAsync_WhenRoleLacksKey_ShouldReturnFalse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("bob", isAdmin: false, permissions: new[] { "首頁" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.False(await checker.HasPermissionAsync(user.Id, "專案項目"));
    }

    [Fact]
    public async Task HasPermissionAsync_LegacyBarePageKey_ShouldGrantAnyActionOfThatPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("legacy", isAdmin: false, permissions: new[] { "專案項目" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目:edit"));
        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目:view"));
        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目:delete"));
    }

    [Fact]
    public async Task HasPermissionAsync_GranularViewOnly_ShouldNotGrantEdit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("viewer", isAdmin: false, permissions: new[] { "專案項目:view" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目:view"));
        Assert.False(await checker.HasPermissionAsync(user.Id, "專案項目:edit"));
        Assert.False(await checker.HasPermissionAsync(user.Id, "專案項目")); // 無裸鍵
    }

    [Fact]
    public async Task HasPermissionAsync_ForUnknownUser_ShouldReturnFalse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var checker = new PermissionChecker(fixture.Context);

        Assert.False(await checker.HasPermissionAsync(999, "首頁"));
    }

    [Fact]
    public async Task GetEffectivePermissionKeysAsync_ShouldReturnRoleKeys()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("carol", isAdmin: false, permissions: new[] { "首頁", "分類清單" });
        var checker = new PermissionChecker(fixture.Context);

        var keys = await checker.GetEffectivePermissionKeysAsync(user.Id);

        Assert.Contains("首頁", keys);
        Assert.Contains("分類清單", keys);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task GetEffectivePermissionKeysAsync_WithMultipleRoles_ShouldReturnUnion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(
            "dave",
            new[] { "首頁" },
            new[] { "專案項目", "首頁" });
        var checker = new PermissionChecker(fixture.Context);

        var keys = await checker.GetEffectivePermissionKeysAsync(user.Id);

        Assert.Contains("首頁", keys);
        Assert.Contains("專案項目", keys);
        Assert.Equal(2, keys.Count);
        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目"));
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
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public async Task<MyUser> AddUserAsync(string account, bool isAdmin, string[] permissions)
        {
            var writer = new RbacWriteService(Context);

            var role = new RoleView
            {
                Name = account + "-role",
                TabViewJson = JsonSerializer.Serialize(permissions),
            };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(role.Id, permissions);

            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                IsAdmin = isAdmin,
                RoleViewId = role.Id,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { role.Id });
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<MyUser> AddMultiRoleUserAsync(string account, string[] roleAPermissions, string[] roleBPermissions)
        {
            var writer = new RbacWriteService(Context);

            var roleA = new RoleView { Name = account + "-A", TabViewJson = JsonSerializer.Serialize(roleAPermissions) };
            var roleB = new RoleView { Name = account + "-B", TabViewJson = JsonSerializer.Serialize(roleBPermissions) };
            Context.RoleView.AddRange(roleA, roleB);
            await Context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(roleA.Id, roleAPermissions);
            await writer.SyncRolePermissionsAsync(roleB.Id, roleBPermissions);

            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                IsAdmin = false,
                RoleViewId = roleA.Id,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { roleA.Id, roleB.Id });
            Context.ChangeTracker.Clear();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
