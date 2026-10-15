using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

public sealed class RbacBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_ShouldCreatePermissionCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RunAsync();

        var keys = await fixture.Context.Permission.AsNoTracking().Select(x => x.Key).ToListAsync();
        Assert.Contains(MagicObjectHelper.角色_會議紀錄, keys);
        Assert.Contains(MagicObjectHelper.角色_專案項目, keys);
        Assert.Contains(MagicObjectHelper.角色_使用者管理, keys);
    }

    [Fact]
    public async Task RunAsync_ShouldLinkRolePermissionsFromTabViewJson()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(
            "檢視員",
            new[] { MagicObjectHelper.角色_會議紀錄, MagicObjectHelper.角色_專案項目 });
        var service = fixture.CreateService();

        await service.RunAsync();

        var linkedKeys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();

        Assert.Equal(2, linkedKeys.Count);
        Assert.Contains(MagicObjectHelper.角色_會議紀錄, linkedKeys);
        Assert.Contains(MagicObjectHelper.角色_專案項目, linkedKeys);
    }

    [Fact]
    public async Task RunAsync_ShouldCreateUserRoleFromRoleViewId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", new[] { MagicObjectHelper.角色_會議紀錄 });
        var user = await fixture.AddUserAsync("alice", role.Id);
        var service = fixture.CreateService();

        await service.RunAsync();

        var userRole = await fixture.Context.UserRole.AsNoTracking()
            .SingleAsync(x => x.MyUserId == user.Id);
        Assert.Equal(role.Id, userRole.RoleViewId);
    }

    [Fact]
    public async Task RunAsync_ShouldRenameLegacyTeamListKeyWithoutLosingGrants()
    {
        // 0.4.101 把「團隊清單」改名「資料群組」，0.4.102 改回來。已勾的角色（含動作鍵）改名後仍要有權限。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("主管", new[] { "資料群組", "資料群組:edit" });
        // 0.4.101 的資料庫：權限表裡是「資料群組」，角色已經勾了。
        await new RbacWriteService(fixture.Context).SyncRolePermissionsAsync(role.Id, ["資料群組", "資料群組:edit"]);
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateService().RunAsync();

        var keys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();
        Assert.Contains(MagicObjectHelper.角色_團隊清單, keys);
        Assert.Contains($"{MagicObjectHelper.角色_團隊清單}:edit", keys);
        Assert.False(await fixture.Context.Permission.AnyAsync(x => x.Key.StartsWith("資料群組")));
        var stored = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == role.Id);
        Assert.DoesNotContain("資料群組", stored.TabViewJson);
    }

    [Theory]
    [InlineData(MagicObjectHelper.已退役權限鍵_系統健康度)]
    [InlineData(MagicObjectHelper.已退役權限鍵_AI用量分析)]
    public async Task RunAsync_ShouldRetireLegacyKeyWithoutTouchingOtherGrants(string legacy)
    {
        // 0.4.93～0.4.119 的「系統健康度」、0.4.80～1.0.29 的「AI 用量分析」可以勾給角色；
        // 之後改為只限管理者（系統健康監控、Token 用量），權限列、對應與勾選都要清掉。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("主管", [legacy, $"{legacy}:view", MagicObjectHelper.角色_會議紀錄]);
        // 0.4.119 的資料庫：權限表裡有舊鍵，角色已經勾了。
        fixture.Context.Permission.AddRange(
            new Permission { Key = legacy, DisplayName = legacy, GroupName = legacy },
            new Permission { Key = $"{legacy}:view", DisplayName = legacy, GroupName = legacy });
        await fixture.Context.SaveChangesAsync();
        await new RbacWriteService(fixture.Context).SyncRolePermissionsAsync(role.Id, [legacy, $"{legacy}:view"]);
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateService().RunAsync();
        await fixture.CreateService().RunAsync();

        Assert.False(await fixture.Context.Permission.AnyAsync(x => x.Key.StartsWith(legacy)));
        var linkedKeys = await fixture.Context.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == role.Id)
            .Join(fixture.Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();
        Assert.Equal([MagicObjectHelper.角色_會議紀錄], linkedKeys);
        var stored = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == role.Id);
        Assert.Equal([MagicObjectHelper.角色_會議紀錄], JsonSerializer.Deserialize<List<string>>(stored.TabViewJson));
    }

    [Fact]
    public async Task RunAsync_ShouldBeIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("甲", new[] { MagicObjectHelper.角色_會議紀錄 });
        var user = await fixture.AddUserAsync("bob", role.Id);
        var service = fixture.CreateService();

        await service.RunAsync();
        await service.RunAsync();

        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == user.Id));
        Assert.Equal(1, await fixture.Context.RolePermissionMap.CountAsync(x => x.RoleViewId == role.Id));
        Assert.Equal(
            1,
            await fixture.Context.Permission.CountAsync(x => x.Key == MagicObjectHelper.角色_會議紀錄));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
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

        public RbacBackfillService CreateService()
            => new(Context, new RolePermissionService(), loggerFactory.CreateLogger<RbacBackfillService>());

        public async Task<RoleView> AddRoleAsync(string name, string[] permissions)
        {
            var role = new RoleView
            {
                Name = name,
                TabViewJson = JsonSerializer.Serialize(permissions),
            };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<MyUser> AddUserAsync(string account, int roleViewId)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                RoleViewId = roleViewId,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
