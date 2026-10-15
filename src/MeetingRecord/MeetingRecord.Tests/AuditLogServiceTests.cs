using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

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

    /// <summary>
    /// 1.0.32：寫入改用自己新建的 context。呼叫端 context 裡還沒存的變更不能被順便存進去
    /// （以前共用 Blazor 連線的 context 時就會）。
    /// </summary>
    [Fact]
    public async Task WriteAsync_ShouldNotFlushCallerPendingChanges()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        fixture.Context.AuditLog.Add(new AuditLog { Action = "Pending.NotSaved", OccurredAt = DateTime.UtcNow });

        await fixture.CreateService().WriteAsync(AuditActions.LoginSuccess, actorUserId: 1, actorAccount: "alice");

        await using var fresh = new BackendDBContext(TestAuditLogs.OptionsOf(fixture.Context));
        Assert.Equal([AuditActions.LoginSuccess], await fresh.AuditLog.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task WriteAsync_ShouldRecordClientIpFromProvider()
    {
        await using var fixture = await AuditFixture.CreateAsync();

        await fixture.CreateService(new FixedClientIpProvider("203.0.113.9")).WriteAsync(AuditActions.LoginSuccess);

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("203.0.113.9", saved.ClientIp);
    }

    [Fact]
    public async Task WriteAsync_WhenIpProviderThrows_ShouldStillWriteWithoutIp()
    {
        await using var fixture = await AuditFixture.CreateAsync();

        await fixture.CreateService(new ThrowingClientIpProvider()).WriteAsync(AuditActions.LoginSuccess);

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Null(saved.ClientIp);
    }

    private sealed class FixedClientIpProvider(string ip) : IClientIpProvider
    {
        public string? GetClientIp() => ip;
    }

    private sealed class ThrowingClientIpProvider : IClientIpProvider
    {
        public string? GetClientIp() => throw new InvalidOperationException("no connection");
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

        public AuditLogService CreateService(IClientIpProvider? clientIpProvider = null)
        {
            return TestAuditLogs.CreateService(Context, clientIpProvider, loggerFactory.CreateLogger<AuditLogService>());
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
