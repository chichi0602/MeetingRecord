using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

public sealed class MyUserServicePasswordTests
{
    #region 管理者給的密碼要先改（0.4.113）

    private static async Task<int> AddRoleAsync(MyUserServiceFixture fixture)
    {
        var role = new RoleView { Name = "一般使用者", TabViewJson = "[]" };
        fixture.Context.RoleView.Add(role);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        return role.Id;
    }

    [Fact]
    public async Task AddAsync_NewAccount_MustChangePasswordUntilChanged()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var roleId = await AddRoleAsync(fixture);
        var service = fixture.CreateService();
        var model = new MyUserAdapterModel { Account = "newbie", Name = "newbie", Password = "given-by-admin", RoleViewId = roleId };

        Assert.True((await service.AddAsync(model)).Success);
        Assert.True(await service.NeedChangePasswordAsync(model));

        Assert.True((await service.ChangeOwnPasswordAsync(model.Id, "given-by-admin", "my-own-password", "my-own-password")).Success);
        Assert.False(await service.NeedChangePasswordAsync(model));
    }

    [Fact]
    public async Task UpdateAsync_AdminResetsOthersPassword_ShouldRequireChange_ButOwnEditShouldNot()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var roleId = await AddRoleAsync(fixture);
        var user = await fixture.AddUserAsync("alice", "old-password");
        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleViewId, roleId));

        // 管理者（別人）重設密碼 → 要改。
        var adminService = fixture.CreateService(currentUserId: 999);
        var model = await adminService.GetAsync(user.Id);
        model.Password = "reset-by-admin";
        Assert.True((await adminService.UpdateAsync(model)).Success);
        Assert.True(await adminService.NeedChangePasswordAsync(model));

        // 本人改完之後，再用使用者管理改自己的資料（含密碼）不會再被要求改。
        Assert.True((await adminService.ChangeOwnPasswordAsync(user.Id, "reset-by-admin", "mine-1", "mine-1")).Success);
        var selfService = fixture.CreateService(currentUserId: user.Id);
        var own = await selfService.GetAsync(user.Id);
        own.Password = "mine-2";
        Assert.True((await selfService.UpdateAsync(own)).Success);
        Assert.False(await selfService.NeedChangePasswordAsync(own));
    }

    [Fact]
    public async Task UpdateAsync_WithoutNewPassword_ShouldKeepPendingFlag()
    {
        // 畫面模型沒有這個欄位；整筆蓋回時不能把「要改密碼」清掉。
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var roleId = await AddRoleAsync(fixture);
        var service = fixture.CreateService();
        var model = new MyUserAdapterModel { Account = "bob", Name = "bob", Password = "given", RoleViewId = roleId };
        await service.AddAsync(model);

        var edit = await service.GetAsync(model.Id);
        edit.Name = "Bob 改名";
        edit.Password = string.Empty;
        Assert.True((await service.UpdateAsync(edit)).Success);

        Assert.True(await service.NeedChangePasswordAsync(edit));
    }

    [Fact]
    public async Task AddAsync_SupportAccount_ShouldNotRequireChange()
    {
        // support 被禁止改密碼，設了會卡在改密碼頁出不去。
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var roleId = await AddRoleAsync(fixture);
        var service = fixture.CreateService();
        var model = new MyUserAdapterModel { Account = MagicObjectHelper.開發者帳號, Name = "support", Password = "support-pass", RoleViewId = roleId };

        await service.AddAsync(model);

        Assert.False(await service.NeedChangePasswordAsync(model));
    }

    #endregion

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithCorrectCurrentPassword_ShouldUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", "new-password", "new-password");

        Assert.True(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.StartsWith("PBKDF2", savedUser.Password);
        Assert.Equal(
            PasswordVerificationOutcome.Success,
            SecurePasswordHasher.VerifyPassword("new-password", savedUser.Password, savedUser.Salt));
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithWrongCurrentPassword_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "wrong-password", "new-password", "new-password");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithBlankNewPassword_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", " ", " ");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_WithMismatchedConfirmation_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", "old-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "old-password", "new-password", "different-password");

        Assert.False(result.Success);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    [Fact]
    public async Task ChangeOwnPasswordAsync_ForSupportAccount_ShouldNotUpdatePassword()
    {
        await using var fixture = await MyUserServiceFixture.CreateAsync();
        var user = await fixture.AddUserAsync(MagicObjectHelper.開發者帳號, "support-password");
        var originalPassword = user.Password;
        var service = fixture.CreateService();

        var result = await service.ChangeOwnPasswordAsync(user.Id, "support-password", "new-password", "new-password");

        Assert.False(result.Success);
        Assert.Contains("support", result.Message, StringComparison.OrdinalIgnoreCase);
        var savedUser = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(originalPassword, savedUser.Password);
    }

    private sealed class MyUserServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private MyUserServiceFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<MyUserServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new MyUserServiceFixture(connection, context);
        }

        public MyUserService CreateService(int currentUserId = 0)
        {
            var currentUserService = new CurrentUserService();
            currentUserService.CurrentUser.Id = currentUserId;
            return new MyUserService(
                Context,
                mapper,
                loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context),
                new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>()),
                currentUserService);
        }

        public async Task<MyUser> AddUserAsync(string account, string password)
        {
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Salt = Guid.NewGuid().ToString(),
                Status = true
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
}
