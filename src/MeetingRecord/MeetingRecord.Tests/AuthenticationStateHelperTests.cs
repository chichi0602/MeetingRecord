using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using System.Security.Claims;

namespace MeetingRecord.Tests;

public sealed class AuthenticationStateHelperTests
{
    [Fact]
    public async Task Check_WithUnauthenticatedPrincipal_ShouldReturnUnauthenticatedAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Unauthenticated, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidSidClaim_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("not-a-number"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("999"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithDisabledUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(status: false);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingRoleView_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserWithoutRoleAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidRoleJson_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(roleJson: "not-json");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOutsideChangePasswordPage_ShouldReturnRequiresPasswordChange()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOnChangePasswordPageWithQuery_ShouldSucceed()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/ChangePassword?returnUrl=/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Null(navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithCachedUserStillRechecksPasswordChangeRequirement()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        fixture.CurrentUserService.CurrentUser.IsAuthenticated = true;
        fixture.CurrentUserService.CurrentUser.Id = user.Id;
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithValidUser_ShouldInitializeCurrentUser()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(permissionName: "PermissionA");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Equal(user.Id, fixture.CurrentUserService.CurrentUser.Id);
        Assert.True(fixture.CurrentUserService.CurrentUser.IsAuthenticated);
        Assert.Contains("PermissionA", fixture.CurrentUserService.CurrentUser.RoleList);
        Assert.Null(navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMultipleRoles_ShouldInitializeRoleListAsUnion()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(primaryKey: "PermissionA", additionalKey: "PermissionB");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        var roleList = fixture.CurrentUserService.CurrentUser.RoleList;
        Assert.Contains("PermissionA", roleList);
        Assert.Contains("PermissionB", roleList);
    }

    private static ClaimsPrincipal CreatePrincipal(string sid)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, sid)],
            "Test");

        return new ClaimsPrincipal(identity);
    }

    private sealed class AuthenticationStateHelperFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private AuthenticationStateHelperFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            CurrentUserService = new CurrentUserService();

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public CurrentUserService CurrentUserService { get; }

        public static async Task<AuthenticationStateHelperFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new AuthenticationStateHelperFixture(connection, context);
        }

        public AuthenticationStateHelper CreateHelper()
        {
            var rolePermissionService = new RolePermissionService();

            return new AuthenticationStateHelper(
                loggerFactory.CreateLogger<AuthenticationStateHelper>(),
                mapper,
                new MyUserService(Context, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(Context), new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>()), CurrentUserService),
                CurrentUserService,
                rolePermissionService,
                new EffectiveTeamResolver(Context),
                new PermissionChecker(Context));
        }

        public async Task<MyUser> AddUserAsync(
            bool status = true,
            string password = "secure-password",
            string roleJson = """["PermissionA"]""",
            string permissionName = "PermissionA")
        {
            var roleView = new RoleView
            {
                Name = $"role-{Guid.NewGuid():N}",
                TabViewJson = roleJson == """["PermissionA"]"""
                    ? $"""["{permissionName}"]"""
                    : roleJson
            };

            Context.RoleView.Add(roleView);
            await Context.SaveChangesAsync();

            // UI 權限來源已改讀 RBAC 表（IPermissionChecker），故種子資料需雙寫 RolePermissionMap，
            // 使登入後 CurrentUser.RoleList 反映此角色的權限鍵。
            await new RbacWriteService(Context).SyncRolePermissionsAsync(roleView.Id, new[] { permissionName });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = status,
                RoleViewId = roleView.Id
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task<MyUser> AddMultiRoleUserAsync(string primaryKey, string additionalKey)
        {
            var writer = new RbacWriteService(Context);

            var primaryRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{primaryKey}"]""" };
            var additionalRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{additionalKey}"]""" };
            Context.RoleView.AddRange(primaryRole, additionalRole);
            await Context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(primaryRole.Id, new[] { primaryKey });
            await writer.SyncRolePermissionsAsync(additionalRole.Id, new[] { additionalKey });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true,
                RoleViewId = primaryRole.Id
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { primaryRole.Id, additionalRole.Id });
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task<MyUser> AddUserWithoutRoleAsync()
        {
            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");

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

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal principal;

        public TestAuthenticationStateProvider(ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(new AuthenticationState(principal));
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string initialUri)
        {
            Initialize("http://localhost/", initialUri);
        }

        public string? NavigatedTo { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedTo = uri;
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}
