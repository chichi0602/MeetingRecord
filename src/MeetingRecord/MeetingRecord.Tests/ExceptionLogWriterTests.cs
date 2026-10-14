using System.Threading.Channels;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 例外紀錄背景寫入器的關機行為。
/// 0.9.77 之前收到停止訊號就直接結束，佇列裡剛記下的例外（包括關機過程本身的錯誤）就此遺失。
/// </summary>
public sealed class ExceptionLogWriterTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string exceptionPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
    private readonly Channel<ExceptionLogEntry> channel = Channel.CreateUnbounded<ExceptionLogEntry>();
    private ServiceProvider? services;

    [Fact]
    public async Task RunAsync_WhenStopped_ShouldDrainQueuedEntries()
    {
        var writer = await CreateWriterAsync(ExceptionLogWriter.DefaultDrainTimeout);
        EnqueueDistinctEntries(3);

        // 已取消的 token：主迴圈一筆都不會讀，三筆全靠關機時的清空寫入。
        await writer.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(3, await CountRowsAsync());
        Assert.Equal(0, channel.Reader.Count);
    }

    [Fact]
    public async Task RunAsync_WhenDrainTimeoutElapsed_ShouldStopWithoutWriting()
    {
        // 資料庫卡住時不可拖住整個關機流程：逾時就放棄，剩餘筆數只輸出到 InternalLogger。
        var writer = await CreateWriterAsync(TimeSpan.Zero);
        EnqueueDistinctEntries(2);

        await writer.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(0, await CountRowsAsync());
        Assert.Equal(2, channel.Reader.Count);
    }

    [Fact]
    public async Task RunAsync_WhenRecordFails_ShouldCountWriteFailure()
    {
        // RecordAsync 自己吞掉錯誤並回傳 null，寫入失敗只能從寫入器看出來，必須計進管線監控。
        var monitor = new LoggingPipelineMonitor();
        var writer = await CreateWriterAsync(ExceptionLogWriter.DefaultDrainTimeout, monitor);
        EnqueueDistinctEntries(2);
        await connection.CloseAsync();

        await writer.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(2, monitor.Snapshot().WriteFailures);
        Assert.NotNull(monitor.Snapshot().LastWriteFailureAt);
    }

    private void EnqueueDistinctEntries(int count)
    {
        for (var index = 0; index < count; index++)
        {
            Assert.True(channel.Writer.TryWrite(new ExceptionLogEntry
            {
                ExceptionType = "System.InvalidOperationException",
                Message = $"boom-{index}",
                StackTrace = "stack",
                Source = ExceptionSources.Process,
                Operation = "Something failed.",
                OccurredAt = DateTime.Now,
            }));
        }
    }

    private async Task<ExceptionLogWriter> CreateWriterAsync(TimeSpan drainTimeout, LoggingPipelineMonitor? monitor = null)
    {
        await connection.OpenAsync();
        await using (var context = new TestDbContextFactory(connection).CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        Directory.CreateDirectory(exceptionPath);
        var settings = new SystemSettings();
        settings.ExternalFileSystem.ExceptionPath = exceptionPath;

        var collection = new ServiceCollection();
        collection.AddScoped(_ => new TestDbContextFactory(connection).CreateDbContext());
        collection.AddSingleton<IMapper>(new MapperConfiguration(
            configuration => configuration.AddProfile<AutoMapping>(),
            NullLoggerFactory.Instance).CreateMapper());
        collection.AddSingleton<ILogger<ExceptionLogService>>(NullLogger<ExceptionLogService>.Instance);
        collection.AddSingleton(Options.Create(settings));
        collection.AddScoped<ExceptionStackFileStore>();
        collection.AddScoped<ExceptionLogService>();
        services = collection.BuildServiceProvider();

        return new ExceptionLogWriter(
            channel.Reader,
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExceptionContextAccessor(),
            drainTimeout,
            monitor: monitor);
    }

    private async Task<int> CountRowsAsync()
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        return await context.ExceptionLog.CountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }

        await connection.DisposeAsync();

        try
        {
            Directory.Delete(exceptionPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // 測試沒有建立任何檔案。
        }
    }
}
