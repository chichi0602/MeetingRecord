using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class MyUserServiceLoginTests
{
    [Fact]
    public async Task LoginAsync_WithNewFormatHash_AndCorrectPassword_ShouldSucceed()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("alice", "secret-password", legacy: false);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("alice", "secret-password");

        Assert.Equal(string.Empty, error);
        Assert.NotNull(user);
    }

    [Fact]
    public async Task LoginAsync_WithLegacyHash_AndCorrectPassword_ShouldUpgradeStoredHash()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        var created = await fixture.AddUserAsync("bob", "secret-password", legacy: true);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("bob", "secret-password");

        Assert.Equal(string.Empty, error);
        Assert.NotNull(user);

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.StartsWith("PBKDF2", saved.Password);
        Assert.Equal(
            PasswordVerificationOutcome.Success,
            SecurePasswordHasher.VerifyPassword("secret-password", saved.Password, saved.Salt));
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ShouldFail()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("carol", "secret-password", legacy: false);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("carol", "wrong-password");

        Assert.NotEqual(string.Empty, error);
        Assert.Null(user);
    }

    [Fact]
    public async Task LoginAsync_AfterFiveWrongPasswords_ShouldLockAccount_AndRejectCorrectPassword()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("dave", "secret-password", legacy: false);
        var service = fixture.CreateService();

        for (int i = 0; i < 5; i++)
        {
            await service.LoginAsync("dave", "wrong-password");
        }

        var (error, user) = await service.LoginAsync("dave", "secret-password");

        Assert.Null(user);
        Assert.Contains("鎖定", error);
    }

    [Fact]
    public async Task LoginAsync_SuccessfulLogin_ShouldResetFailedCount()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        var created = await fixture.AddUserAsync("erin", "secret-password", legacy: false);
        var service = fixture.CreateService();

        await service.LoginAsync("erin", "wrong-password");
        await service.LoginAsync("erin", "wrong-password");
        await service.LoginAsync("erin", "secret-password");

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(0, saved.AccessFailedCount);
        Assert.Null(saved.LockoutEndUtc);
    }

    [Fact]
    public async Task LoginAsync_Success_ShouldWriteAuditLog()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("grace", "secret-password", legacy: false);
        var service = fixture.CreateService();

        await service.LoginAsync("grace", "secret-password");

        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Login.Success", audit.Action);
        Assert.True(audit.Success);
        Assert.Equal("grace", audit.ActorAccount);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ShouldWriteFailedAuditLog()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("heidi", "secret-password", legacy: false);
        var service = fixture.CreateService();

        await service.LoginAsync("heidi", "wrong-password");

        var audit = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Login.Failed", audit.Action);
        Assert.False(audit.Success);
    }

    [Fact]
    public async Task LoginAsync_WithExpiredLockout_ShouldAllowLogin()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        var created = await fixture.AddUserAsync("frank", "secret-password", legacy: false);
        await fixture.SetLockoutAsync(created.Id, DateTime.UtcNow.AddMinutes(-1), failedCount: 5);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("frank", "secret-password");

        Assert.Equal(string.Empty, error);
        Assert.NotNull(user);
    }

    private sealed class LoginFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private LoginFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<LoginFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new LoginFixture(connection, context);
        }

        public MyUserServiceLogin CreateService()
        {
            return new MyUserServiceLogin(
                Context,
                mapper,
                new ConfigurationBuilder().Build(),
                loggerFactory.CreateLogger<MyUserServiceLogin>(),
                new RolePermissionService(),
                new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>()));
        }

        public async Task<MyUser> AddUserAsync(string account, string password, bool legacy)
        {
            var salt = Guid.NewGuid().ToString();
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Salt = salt,
                Status = true,
                Password = legacy
                    ? PasswordHelper.GetPasswordSHA(salt, password)
                    : SecurePasswordHasher.HashPassword(password),
            };

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task SetLockoutAsync(int userId, DateTime lockoutEndUtc, int failedCount)
        {
            var user = await Context.MyUser.SingleAsync(x => x.Id == userId);
            user.LockoutEndUtc = lockoutEndUtc;
            user.AccessFailedCount = failedCount;
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
