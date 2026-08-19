using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task WriteAsync_ShouldPersistAuditRow()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.WriteAsync(
            action: "Login.Success",
            success: true,
            actorUserId: 7,
            actorAccount: "alice",
            targetType: "MyUser",
            targetId: "7",
            detail: "unit test");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Login.Success", saved.Action);
        Assert.True(saved.Success);
        Assert.Equal(7, saved.ActorUserId);
        Assert.Equal("alice", saved.ActorAccount);
        Assert.Equal("MyUser", saved.TargetType);
        Assert.Equal("7", saved.TargetId);
        Assert.NotEqual(default, saved.OccurredAt);
    }

    [Fact]
    public async Task WriteAsync_WhenPersistFails_ShouldNotThrow()
    {
        var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeAsync(); // context disposed → write will fail internally

        var exception = await Record.ExceptionAsync(() => service.WriteAsync("Login.Failed", success: false));

        Assert.Null(exception);
    }

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private AuditFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public BackendDBContext Context { get; }

        public static async Task<AuditFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new AuditFixture(connection, context);
        }

        public AuditLogService CreateService()
        {
            return new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>());
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
