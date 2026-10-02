using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 刪除角色（0.4.110）：0.4.109 以前只要有人以這個角色當主要角色，資料庫外鍵（Restrict）就擋下刪除，
/// 畫面卻照樣顯示「刪除成功」。現在改為把受影響使用者的主要角色換掉再刪，任何人都不會變成沒有角色
/// （沒有角色的帳號登入時會被登出）。
/// </summary>
public sealed class RoleViewDeleteTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly IMapper mapper;

    public RoleViewDeleteTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
    }

    private RoleViewService CreateService()
        => new(
            context,
            mapper,
            loggerFactory.CreateLogger<RoleViewService>(),
            new RolePermissionService(),
            new RbacWriteService(context),
            new AuditLogService(context, loggerFactory.CreateLogger<AuditLogService>()),
            new CurrentUserService());

    private async Task<RoleView> AddRoleAsync(string name)
    {
        var role = new RoleView { Name = name, TabViewJson = "[]" };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return role;
    }

    private async Task<MyUser> AddUserAsync(string account, int primaryRoleId, params int[] extraRoleIds)
    {
        var user = new MyUser { Account = account, Name = account, Password = "x", Status = true, RoleViewId = primaryRoleId };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        context.UserRole.AddRange(new[] { primaryRoleId }.Concat(extraRoleIds)
            .Select(id => new UserRole { MyUserId = user.Id, RoleViewId = id }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return user;
    }

    private async Task<(int? Primary, List<int> All)> RolesOfAsync(int userId)
    {
        var primary = (await context.MyUser.AsNoTracking().SingleAsync(x => x.Id == userId)).RoleViewId;
        var all = await context.UserRole.AsNoTracking().Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).OrderBy(x => x).ToListAsync();
        return (primary, all);
    }

    [Fact]
    public async Task Delete_RoleUsedAsPrimary_ShouldSucceed_AndSwitchToUsersOtherRole()
    {
        var general = await AddRoleAsync(MagicObjectHelper.預設角色);
        var manager = await AddRoleAsync("主管");
        var viewer = await AddRoleAsync("檢視者");
        var user = await AddUserAsync("alice", manager.Id, viewer.Id);

        var result = await CreateService().DeleteAsync(manager.Id);

        Assert.True(result.Success, result.Message);
        Assert.False(await context.RoleView.AnyAsync(x => x.Id == manager.Id));
        var (primary, all) = await RolesOfAsync(user.Id);
        Assert.Equal(viewer.Id, primary);
        Assert.Equal([viewer.Id], all);
        Assert.True(await context.RoleView.AnyAsync(x => x.Id == general.Id));
    }

    [Fact]
    public async Task Delete_UsersOnlyRole_ShouldFallBackToGeneralUserRole()
    {
        var general = await AddRoleAsync(MagicObjectHelper.預設角色);
        var manager = await AddRoleAsync("主管");
        var user = await AddUserAsync("bob", manager.Id);

        var result = await CreateService().DeleteAsync(manager.Id);

        Assert.True(result.Success, result.Message);
        var (primary, all) = await RolesOfAsync(user.Id);
        Assert.Equal(general.Id, primary);
        Assert.Equal([general.Id], all);
    }

    [Fact]
    public async Task Delete_GeneralUserRoleItself_ShouldFallBackToAnyRemainingRole()
    {
        var general = await AddRoleAsync(MagicObjectHelper.預設角色);
        var viewer = await AddRoleAsync("檢視者");
        var user = await AddUserAsync("carol", general.Id);

        var result = await CreateService().DeleteAsync(general.Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(viewer.Id, (await RolesOfAsync(user.Id)).Primary);
    }

    [Fact]
    public async Task Delete_LastRemainingRole_ShouldBeRejected()
    {
        // 刪掉最後一個角色，所有人（含管理者）都會因為沒有角色而無法登入。
        var only = await AddRoleAsync("唯一角色");
        await AddUserAsync("dave", only.Id);

        var result = await CreateService().DeleteAsync(only.Id);

        Assert.False(result.Success);
        Assert.True(await context.RoleView.AnyAsync(x => x.Id == only.Id));
    }

    [Fact]
    public async Task CountUsers_ShouldCountPrimaryAndAdditionalRoles()
    {
        var general = await AddRoleAsync(MagicObjectHelper.預設角色);
        var viewer = await AddRoleAsync("檢視者");
        await AddUserAsync("erin", general.Id, viewer.Id);
        await AddUserAsync("frank", viewer.Id);

        Assert.Equal(2, await CreateService().CountUsersAsync(viewer.Id));
        Assert.Equal(1, await CreateService().CountUsersAsync(general.Id));
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        loggerFactory.Dispose();
    }
}
