using AutoMapper;
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

namespace MeetingRecord.Tests;

public sealed class MyUserServicePasswordTests
{
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

        public MyUserService CreateService()
        {
            return new MyUserService(
                Context,
                mapper,
                loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context),
                new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>()),
                new CurrentUserService());
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
