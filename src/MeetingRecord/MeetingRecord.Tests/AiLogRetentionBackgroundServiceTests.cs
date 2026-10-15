using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.BackgroundServices;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 對話紀錄（預設 90 天）與用量帳本（預設不清）的自動保留期限（1.0.29）。
/// </summary>
public sealed class AiLogRetentionBackgroundServiceTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0);

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string callLogPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));

    public AiLogRetentionBackgroundServiceTests()
    {
        connection.Open();
        using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
        Directory.CreateDirectory(callLogPath);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(callLogPath))
        {
            Directory.Delete(callLogPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnce_DefaultSettings_ShouldPurgeOldCallLogsButKeepTheLedger()
    {
        await SeedAsync(Now.AddDays(-120));

        var (callLogs, usageRows) = await CreateService(usageDays: 0).RunOnceAsync(Now);

        Assert.Equal(1, callLogs);
        Assert.Equal(0, usageRows);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.False(await context.AiCallLog.AnyAsync());
        Assert.Equal(1, await context.AiUsageLog.CountAsync());
        Assert.Equal(["AiCallLog.AutoPurge"], await context.AuditLog.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task RunOnce_WithLedgerRetention_ShouldPurgeTheLedgerToo()
    {
        await SeedAsync(Now.AddDays(-400));

        var (_, usageRows) = await CreateService(usageDays: 365).RunOnceAsync(Now);

        Assert.Equal(1, usageRows);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.False(await context.AiUsageLog.AnyAsync());
        Assert.Contains("AiUsage.AutoPurge", await context.AuditLog.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task RunOnce_NothingExpired_ShouldNotWriteAudit()
    {
        await SeedAsync(Now.AddDays(-1));

        await CreateService(usageDays: 365).RunOnceAsync(Now);

        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.False(await context.AuditLog.AnyAsync());
    }

    private AiLogRetentionBackgroundService CreateService(int usageDays)
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiCallLogPath = callLogPath;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SystemSettings>>(Options.Create(settings));
        services.AddSingleton<IOptionsMonitor<AiCallLogSettings>>(new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings()));
        services.AddSingleton<IOptions<ExchangeRateSettings>>(Options.Create(new ExchangeRateSettings()));
        services.AddSingleton(new ExchangeRateCache());
        services.AddScoped(_ => new TestDbContextFactory(connection).CreateDbContext());
        services.AddScoped<AiCallLogFileStore>();
        services.AddScoped<AiCallLogService>();
        services.AddScoped<AiUsageAnalysisService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddSingleton(TestAuditLogs.OptionsFor(connection));
        var provider = services.BuildServiceProvider();

        return new AiLogRetentionBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<LogRetentionSettings>(new LogRetentionSettings { AiUsageLogDays = usageDays }),
            new ExceptionContextAccessor(),
            NullLogger<AiLogRetentionBackgroundService>.Instance);
    }

    private async Task SeedAsync(DateTime occurredAt)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        var callId = Guid.NewGuid();
        context.AiCallLog.Add(new AiCallLog
        {
            CallId = callId,
            OccurredAt = occurredAt,
            Feature = AiUsageFeature.AiChat,
            Outcome = AiUsageOutcome.Succeeded,
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
        });
        context.AiUsageLog.Add(new AiUsageLog
        {
            OccurredAt = occurredAt,
            Feature = AiUsageFeature.AiChat,
            Outcome = AiUsageOutcome.Succeeded,
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            CallId = callId,
        });
        await context.SaveChangesAsync();
    }
}
