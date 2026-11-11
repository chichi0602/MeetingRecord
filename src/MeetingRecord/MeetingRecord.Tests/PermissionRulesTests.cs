using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 權限與登入狀態的判斷規則。1.1.46 起取代 <c>AuthenticationStateHelperTests</c>：Blazor Server 頁拆除後，
/// 以前經 <c>Check</c> 驗的規則改成直接驗純函式（預設角色的權限矩陣）與 <see cref="UserSessionService"/>（帳號狀態，Session API 用）。
/// </summary>
public sealed class PermissionRulesTests
{
    #region 預設角色的實際權限（0.4.109）

    private static readonly string[] BusinessPages =
        [MagicObjectHelper.角色_專案項目, MagicObjectHelper.角色_待辦事項, MagicObjectHelper.角色_會議紀錄];

    private static string[] KeysOf(string roleName) => [.. RolePresets.All.Single(x => x.Name == roleName).PermissionKeys];

    [Fact]
    public void Viewer_CanOpenBusinessPages_ButOnlyView()
    {
        // 0.4.108 以前的 bug：只有「頁面:view」的角色連頁面都進不去（只認整頁鍵）。
        var keys = KeysOf("檢視者");

        foreach (var page in BusinessPages)
        {
            Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, page), page);
            Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.View), page);
            Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Create), page);
            Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Edit), page);
            Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Delete), page);
            Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Export), page);
        }

        Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_專案管理));
        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_分類清單));
        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_團隊清單));
        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_使用者管理));
        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_提示詞清單));
    }

    [Fact]
    public void GeneralUser_CanDoEverythingButDelete_OnBusinessPages()
    {
        var keys = KeysOf(MagicObjectHelper.預設角色);

        foreach (var page in BusinessPages)
        {
            Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, page), page);
            Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Create), page);
            Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Edit), page);
            Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Export), page);
            Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Delete), page);
        }

        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_團隊清單));
    }

    [Fact]
    public void Manager_HasAllBusinessActions_AndCategoryTeamPages()
    {
        var keys = KeysOf("主管");

        foreach (var page in BusinessPages)
        {
            Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, page, PermissionActions.Delete), page);
        }

        Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_分類清單));
        // 0.4.114：主管對團隊只能檢視——改別的部門的團隊或成員，會影響別的部門看得到什麼。
        Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_團隊清單));
        Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, MagicObjectHelper.角色_團隊清單, PermissionActions.Create));
        Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, MagicObjectHelper.角色_團隊清單, PermissionActions.Edit));
        Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, MagicObjectHelper.角色_團隊清單, PermissionActions.Delete));
        Assert.False(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_使用者管理));
    }

    [Fact]
    public void AdminFlag_PassesEverything_EvenWithViewerRole()
    {
        var keys = KeysOf("檢視者");

        Assert.True(AuthenticationStateHelper.CanAccessPage(keys, true, MagicObjectHelper.角色_使用者管理));
        Assert.True(AuthenticationStateHelper.CanDoAction(keys, true, MagicObjectHelper.角色_專案項目, PermissionActions.Delete));
    }

    [Fact]
    public void WholePageKey_ShouldMeanEveryAction()
    {
        string[] keys = [MagicObjectHelper.角色_分類清單];

        Assert.True(AuthenticationStateHelper.CanAccessPage(keys, false, MagicObjectHelper.角色_分類清單));
        Assert.True(AuthenticationStateHelper.CanDoAction(keys, false, MagicObjectHelper.角色_分類清單, PermissionActions.Delete));
        Assert.False(AuthenticationStateHelper.CanDoAction(keys, false, MagicObjectHelper.角色_團隊清單, PermissionActions.View));
    }

    #endregion

    #region 帳號狀態（UserSessionService，Session API 用；1.0.33 起各自帶登出原因）

    [Fact]
    public async Task Evaluate_MissingUser_ShouldBeDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();

        var evaluation = await fixture.CreateService().EvaluateAsync(999);

        Assert.Equal(UserSessionStatus.Deleted, evaluation.Status);
        Assert.Equal(LogoutReasons.Deleted, evaluation.LogoutReason);
    }

    [Fact]
    public async Task Evaluate_DisabledUser_ShouldBeDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync(status: false);

        var evaluation = await fixture.CreateService().EvaluateAsync(user.Id);

        Assert.Equal(UserSessionStatus.Disabled, evaluation.Status);
        Assert.Equal(LogoutReasons.Disabled, evaluation.LogoutReason);
    }

    [Fact]
    public async Task Evaluate_UserWithoutRole_ShouldBeNoRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync(withRole: false);

        var evaluation = await fixture.CreateService().EvaluateAsync(user.Id);

        Assert.Equal(UserSessionStatus.NoRole, evaluation.Status);
        Assert.Equal(LogoutReasons.NoRole, evaluation.LogoutReason);
    }

    [Fact]
    public async Task Evaluate_PasswordChangeRequired_ShouldSaySo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);

        var evaluation = await fixture.CreateService().EvaluateAsync(user.Id);

        Assert.Equal(UserSessionStatus.Active, evaluation.Status);
        Assert.True(evaluation.MustChangePassword);
    }

    [Fact]
    public async Task Evaluate_ValidUser_ShouldBeActive()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync();

        var evaluation = await fixture.CreateService().EvaluateAsync(user.Id);

        Assert.Equal(UserSessionStatus.Active, evaluation.Status);
        Assert.False(evaluation.MustChangePassword);
        Assert.Null(evaluation.LogoutReason);
        Assert.Equal(user.Id, evaluation.User!.Id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public UserSessionService CreateService()
            => new(new MyUserService(Context, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context), TestAuditLogs.CreateService(Context), new CurrentUserService()));

        public async Task<MyUser> AddUserAsync(bool status = true, string password = "secure-password", bool withRole = true)
        {
            RoleView? roleView = null;
            if (withRole)
            {
                roleView = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = """["PermissionA"]""" };
                Context.RoleView.Add(roleView);
                await Context.SaveChangesAsync();
            }

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = status,
                RoleViewId = roleView?.Id,
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);
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

    #endregion
}
