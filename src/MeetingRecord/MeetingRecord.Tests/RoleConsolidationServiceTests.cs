using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 角色收斂成「管理者／一般使用者」（0.4.98）。重點是**收得回來**：舊的預設角色擁有全部權限，
/// 只新增不移除的回填做法會讓一般帳號繼續看得到使用者管理、AI 用量這些頁面。
/// </summary>
public sealed class RoleConsolidationServiceTests
{
    [Fact]
    public async Task RunAsync_ShouldRenameLegacyDefaultRoleInPlace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = await fixture.AddRoleAsync(MagicObjectHelper.舊版預設角色, []);

        var roleId = await fixture.CreateService().RunAsync();

        // 沿用同一列：既有帳號的 RoleViewId、預設團隊都掛在它上面。
        Assert.Equal(legacy.Id, roleId);
        var role = await fixture.Context.RoleView.AsNoTracking().SingleAsync(x => x.Id == roleId);
        Assert.Equal(MagicObjectHelper.預設角色, role.Name);
    }

    [Fact]
    public async Task RunAsync_ShouldRevokeAdminOnlyPermissionsFromGeneralRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = await fixture.AddRoleAsync(
            MagicObjectHelper.舊版預設角色,
            [MagicObjectHelper.角色_會議紀錄, MagicObjectHelper.角色_使用者管理, MagicObjectHelper.角色_AI用量分析]);
        await fixture.CreateBackfillService().RunAsync();

        await fixture.CreateService().RunAsync();

        var keys = await fixture.PermissionKeysOfAsync(legacy.Id);
        Assert.Contains(MagicObjectHelper.角色_會議紀錄, keys);
        Assert.Contains(MagicObjectHelper.角色_專案項目, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_使用者管理, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_AI用量分析, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_提示詞清單, keys);
    }

    [Fact]
    public async Task RunAsync_ShouldKeepPermissionsAdjustedInRoleManagement()
    {
        // 權限只在升級當下設定一次；之後以角色管理頁為準，重啟不能蓋回去。
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync(MagicObjectHelper.舊版預設角色, []);
        var roleId = await fixture.CreateService().RunAsync();

        await new RbacWriteService(fixture.Context).SyncRolePermissionsAsync(
            roleId, [MagicObjectHelper.角色_會議紀錄, MagicObjectHelper.角色_提示詞清單]);
        await fixture.CreateService().RunAsync();

        var keys = await fixture.PermissionKeysOfAsync(roleId);
        Assert.Contains(MagicObjectHelper.角色_提示詞清單, keys);
        Assert.DoesNotContain(MagicObjectHelper.角色_專案項目, keys);
    }

    [Fact]
    public async Task RunAsync_ShouldMoveEveryUserToGeneralRoleOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync(MagicObjectHelper.舊版預設角色, []);
        var other = await fixture.AddRoleAsync("主管", [MagicObjectHelper.角色_使用者管理]);
        var user = await fixture.AddUserAsync("alice", other.Id, isAdmin: false);
        var admin = await fixture.AddUserAsync("root", other.Id, isAdmin: true);
        await fixture.CreateBackfillService().RunAsync();

        var roleId = await fixture.CreateService().RunAsync();

        foreach (var id in new[] { user.Id, admin.Id })
        {
            var stored = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(roleId, stored.RoleViewId);

            var roles = await fixture.Context.UserRole.AsNoTracking()
                .Where(x => x.MyUserId == id)
                .Select(x => x.RoleViewId)
                .ToListAsync();
            Assert.Equal([roleId], roles);
        }
    }

    [Fact]
    public async Task RunAsync_WithoutAnyRole_ShouldCreateGeneralRole()
    {
        await using var fixture = await Fixture.CreateAsync();

        var roleId = await fixture.CreateService().RunAsync();

        var role = await fixture.Context.RoleView.AsNoTracking().SingleAsync();
        Assert.Equal(roleId, role.Id);
        Assert.Equal(MagicObjectHelper.預設角色, role.Name);
    }

    [Fact]
    public async Task RunAsync_ShouldBeIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync(MagicObjectHelper.舊版預設角色, []);
        var user = await fixture.AddUserAsync("alice", null, isAdmin: false);

        var first = await fixture.CreateService().RunAsync();
        var second = await fixture.CreateService().RunAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, await fixture.Context.RoleView.CountAsync());
        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == user.Id));
        Assert.Equal(
            new RolePermissionService().GetGeneralUserPermissionNames().Count,
            await fixture.Context.RolePermissionMap.CountAsync(x => x.RoleViewId == first));
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

        public RoleConsolidationService CreateService()
            => new(
                Context,
                new RolePermissionService(),
                new RbacWriteService(Context),
                loggerFactory.CreateLogger<RoleConsolidationService>());

        public RbacBackfillService CreateBackfillService()
            => new(Context, new RolePermissionService(), loggerFactory.CreateLogger<RbacBackfillService>());

        public async Task<RoleView> AddRoleAsync(string name, string[] permissions)
        {
            var role = new RoleView { Name = name, TabViewJson = JsonSerializer.Serialize(permissions) };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role;
        }

        public async Task<MyUser> AddUserAsync(string account, int? roleViewId, bool isAdmin)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                IsAdmin = isAdmin,
                RoleViewId = roleViewId,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<List<string>> PermissionKeysOfAsync(int roleViewId)
            => await Context.RolePermissionMap.AsNoTracking()
                .Where(x => x.RoleViewId == roleViewId)
                .Join(Context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
                .ToListAsync();

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
