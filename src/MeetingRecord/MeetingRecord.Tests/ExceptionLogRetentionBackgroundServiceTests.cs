using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.BackgroundServices;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 系統例外紀錄的自動保留期限（0.4.122）：依「最後發生」（本地時間）刪除，0＝不清，刪了才寫稽核。
/// </summary>
public sealed class ExceptionLogRetentionBackgroundServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string exceptionPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));

    public ExceptionLogRetentionBackgroundServiceTests()
    {
        connection.Open();
        using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();
        Directory.CreateDirectory(exceptionPath);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(exceptionPath))
        {
            Directory.Delete(exceptionPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunOnceAsync_ShouldRemoveOnlyRowsOlderThanRetentionAndAudit()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0);
        await SeedAsync(("old", now.AddDays(-91)), ("recent", now.AddDays(-89)));
        var service = CreateService(days: 90);

        var removed = await service.RunOnceAsync(now);

        Assert.Equal(1, removed);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(["recent"], await context.ExceptionLog.Select(x => x.Message).ToListAsync());
        var audit = await context.AuditLog.SingleAsync();
        Assert.Equal("ExceptionLog.AutoPurge", audit.Action);
        Assert.Contains("rows=1", audit.Detail);
    }

    [Fact]
    public async Task RunOnceAsync_WithZeroDays_ShouldNotDeleteAnything()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0);
        await SeedAsync(("ancient", now.AddYears(-5)));

        var removed = await CreateService(days: 0).RunOnceAsync(now);

        Assert.Equal(0, removed);
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.Equal(1, await context.ExceptionLog.CountAsync());
        Assert.False(await context.AuditLog.AnyAsync());
    }

    [Fact]
    public async Task RunOnceAsync_NothingStale_ShouldNotWriteAudit()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0);
        await SeedAsync(("recent", now.AddDays(-1)));

        await CreateService(days: 90).RunOnceAsync(now);

        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        Assert.False(await context.AuditLog.AnyAsync());
    }

    private ExceptionLogRetentionBackgroundService CreateService(int days)
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.ExceptionPath = exceptionPath;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SystemSettings>>(Options.Create(settings));
        services.AddScoped(_ => new TestDbContextFactory(connection).CreateDbContext());
        services.AddSingleton<IMapper>(_ => new MapperConfiguration(
            configuration => configuration.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper());
        services.AddScoped<ExceptionStackFileStore>();
        services.AddScoped<ExceptionLogService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddSingleton(TestAuditLogs.OptionsFor(connection));
        var provider = services.BuildServiceProvider();

        return new ExceptionLogRetentionBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<LogRetentionSettings>(new LogRetentionSettings { ExceptionLogDays = days }),
            new ExceptionContextAccessor(),
            NullLogger<ExceptionLogRetentionBackgroundService>.Instance);
    }

    private async Task SeedAsync(params (string Message, DateTime LastOccurredAt)[] rows)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        foreach (var (message, lastOccurredAt) in rows)
        {
            context.ExceptionLog.Add(new ExceptionLog
            {
                Signature = Guid.NewGuid().ToString("N"),
                ExceptionType = "System.Exception",
                Message = message,
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = lastOccurredAt,
                LastOccurredAt = lastOccurredAt,
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
