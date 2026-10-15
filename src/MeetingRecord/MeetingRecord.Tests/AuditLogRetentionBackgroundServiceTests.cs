using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.BackgroundServices;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>稽核紀錄的自動保留期限（1.0.32，預設 365 天、門檻用 UTC）。</summary>
public sealed class AuditLogRetentionBackgroundServiceTests : IAsyncDisposable
{
    private static readonly DateTime UtcNow = new(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public AuditLogRetentionBackgroundServiceTests()
    {
        connection.Open();
        using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task RunOnce_ShouldPurgeExpiredRows_AndWriteAutoPurgeAfterwards()
    {
        await SeedAsync(UtcNow.AddDays(-400), UtcNow.AddDays(-10));

        var removed = await CreateService(days: 365).RunOnceAsync(UtcNow);

        Assert.Equal(1, removed);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        var actions = await context.AuditLog.OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
        Assert.Equal([AuditActions.LoginSuccess, AuditActions.AuditAutoPurge], actions);
        var purge = await context.AuditLog.SingleAsync(x => x.Action == AuditActions.AuditAutoPurge);
        Assert.Null(purge.ActorUserId);
        Assert.Equal("rows=1; days=365", purge.Detail);
    }

    [Fact]
    public async Task RunOnce_WhenDisabled_ShouldKeepEverything()
    {
        await SeedAsync(UtcNow.AddDays(-4000));

        var removed = await CreateService(days: 0).RunOnceAsync(UtcNow);

        Assert.Equal(0, removed);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(1, await context.AuditLog.CountAsync());
    }

    [Fact]
    public async Task RunOnce_NothingExpired_ShouldNotWriteAudit()
    {
        await SeedAsync(UtcNow.AddDays(-1));

        await CreateService(days: 365).RunOnceAsync(UtcNow);

        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal([AuditActions.LoginSuccess], await context.AuditLog.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public void Settings_ManualDays_ShouldFallBackToDefaultWhenAutoPurgeIsDisabled()
    {
        Assert.Equal(365, new LogRetentionSettings().AuditLogDays);
        Assert.Equal(90, new LogRetentionSettings { AuditLogDays = 90 }.ManualAuditLogDays);
        Assert.Equal(365, new LogRetentionSettings { AuditLogDays = 0 }.ManualAuditLogDays);
    }

    private AuditLogRetentionBackgroundService CreateService(int days)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BackendDBContext>(_ => new TestDbContextFactory(connection).CreateDbContext());
        services.AddSingleton(TestAuditLogs.OptionsFor(connection));
        services.AddScoped<AuditLogQueryService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        var provider = services.BuildServiceProvider();

        return new AuditLogRetentionBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<LogRetentionSettings>(new LogRetentionSettings { AuditLogDays = days }),
            new ExceptionContextAccessor(),
            NullLogger<AuditLogRetentionBackgroundService>.Instance);
    }

    private async Task SeedAsync(params DateTime[] occurredAtUtc)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.AuditLog.AddRange(occurredAtUtc.Select(x => new AuditLog { OccurredAt = x, Action = AuditActions.LoginSuccess }));
        await context.SaveChangesAsync();
    }
}
