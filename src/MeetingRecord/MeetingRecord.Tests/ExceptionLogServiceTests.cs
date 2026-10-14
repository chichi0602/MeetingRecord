using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 系統例外紀錄的服務層。
///
/// 重點在三件事：
/// 1. 相同例外合併成一列並累加次數（而不是每次都新增一列）。
/// 2. 堆疊檔案只在首次發生時寫一次 —— AI 主機逾時重複數百次時，不可以寫數百次檔。
/// 3. 刪除資料列一定要同時刪掉堆疊檔，不可留下孤兒檔
///    （專案附件就曾因為忘了這件事而留下孤兒檔）。
/// </summary>
public sealed class ExceptionLogServiceTests
{
    [Fact]
    public async Task RecordAsync_NewException_ShouldInsertRowAndWriteStackFile()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(1, row.OccurrenceCount);
        Assert.Equal("System.NullReferenceException", row.ExceptionType);
        Assert.Equal(ExceptionSources.Ui, row.Source);
        Assert.Equal("/categories", row.Page);
        Assert.Equal("Failed to create category. Name={CategoryName}", row.Operation);
        Assert.Equal("support", row.Account);

        Assert.NotNull(row.StackTraceFile);
        Assert.True(File.Exists(fixture.FullPath(row.StackTraceFile!)));
        Assert.Contains("stack-trace-content", await File.ReadAllTextAsync(fixture.FullPath(row.StackTraceFile!)));
    }

    [Fact]
    public async Task RecordAsync_ShouldKeepLastTraceId_AndReturnOutcome()
    {
        // 例外紀錄要帶得出「最後一次」的錯誤追蹤碼；回傳值是寫入器判斷寫入失敗的依據。
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        var first = await service.RecordAsync(NewEntry(traceId: "AAAA1111"));
        var second = await service.RecordAsync(NewEntry(traceId: "BBBB2222", isCritical: true));

        Assert.NotNull(first);
        Assert.True(first.IsNew);
        Assert.Equal(1, first.OccurrenceCount);
        Assert.NotNull(second);
        Assert.False(second.IsNew);
        Assert.Equal(2, second.OccurrenceCount);
        Assert.True(second.IsCritical);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("BBBB2222", Assert.Single(await fixture.ListAsync()).LastTraceId);
    }

    [Fact]
    public async Task RecordAsync_SameException_ShouldMergeAndIncrementCount()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());
        await service.RecordAsync(NewEntry());
        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(3, row.OccurrenceCount);
    }

    [Fact]
    public async Task RecordAsync_RepeatedException_ShouldNotRewriteStackFile()
    {
        // 大語言模型主機逾時會在短時間內重複數百次。堆疊只寫首次那一份，
        // 寫檔成本必須與發生次數無關。
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry());
        var row = Assert.Single(await fixture.ListAsync());
        var path = fixture.FullPath(row.StackTraceFile!);
        var firstWriteTime = File.GetLastWriteTimeUtc(path);

        await Task.Delay(30);
        await service.RecordAsync(NewEntry(stackTrace: "different-content-should-be-ignored"));

        Assert.Equal(firstWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.DoesNotContain("different-content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RecordAsync_DifferentPageOrOperation_ShouldCreateSeparateRows()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(page: "/categories"));
        await service.RecordAsync(NewEntry(page: "/projects"));
        await service.RecordAsync(NewEntry(operation: "Failed to delete category."));

        Assert.Equal(3, (await fixture.ListAsync()).Count);
    }

    [Fact]
    public async Task RecordAsync_WhenRowLimitReached_ShouldRecordOverflowRowInstead()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        await fixture.SeedRowsAsync(ExceptionLogService.MaxRows);
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(message: "brand-new-1"));
        await service.RecordAsync(NewEntry(message: "brand-new-2"));

        var rows = await fixture.ListAsync();
        Assert.Equal(ExceptionLogService.MaxRows + 1, rows.Count);

        var overflow = Assert.Single(rows, x => x.Signature == ExceptionSignature.OverflowSignature);
        Assert.Equal(2, overflow.OccurrenceCount);

        // 兩筆新例外都沒有變成獨立列。
        Assert.DoesNotContain(rows, x => x.Message == "brand-new-1");
    }

    [Fact]
    public async Task RecordAsync_ShouldNeverThrow()
    {
        // 本服務位在例外記錄管線之內，任何失敗都不得往外拋。
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeConnectionAsync();

        var exception = await Record.ExceptionAsync(() => service.RecordAsync(NewEntry()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRowAndStackFile()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        var path = fixture.FullPath(row.StackTraceFile!);
        Assert.True(File.Exists(path));

        var result = await service.DeleteAsync(row.Id);

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ClearAllAsync_ShouldEmptyTableAndDirectory()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry(page: "/a"));
        await service.RecordAsync(NewEntry(page: "/b"));

        var result = await service.ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ExceptionPath));
    }

    [Fact]
    public async Task PurgeAsync_ShouldRemoveOnlyStaleRowsAndTheirFiles()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(page: "/fresh"));
        await service.RecordAsync(NewEntry(page: "/stale"));
        await fixture.MakeStaleAsync("/stale", DateTime.Now.AddDays(-120));

        var staleRow = (await fixture.ListAsync()).Single(x => x.Page == "/stale");
        var stalePath = fixture.FullPath(staleRow.StackTraceFile!);

        var result = await service.PurgeAsync(90);

        Assert.True(result.Success);
        var remaining = Assert.Single(await fixture.ListAsync());
        Assert.Equal("/fresh", remaining.Page);
        Assert.False(File.Exists(stalePath));
    }

    [Fact]
    public async Task GetAsync_ShouldDefaultToLastOccurredDescending()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(page: "/old", occurredAt: DateTime.Now.AddHours(-2)));
        await service.RecordAsync(NewEntry(page: "/new", occurredAt: DateTime.Now));

        var result = await service.GetAsync(new ExceptionLogQuery { PageSize = 10 });

        Assert.Equal(2, result.Count);
        Assert.Equal("/new", result.Result.First().Page);
    }

    [Fact]
    public async Task GetAsync_ShouldFilterBySourceAccountAndKeyword()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.RecordAsync(NewEntry(page: "/ui", source: ExceptionSources.Ui, account: "alice"));
        await service.RecordAsync(NewEntry(page: "/api", source: ExceptionSources.WebApi, account: "bob"));

        var bySource = await service.GetAsync(new ExceptionLogQuery { Source = ExceptionSources.WebApi, PageSize = 10 });
        Assert.Equal("/api", Assert.Single(bySource.Result).Page);

        var byAccount = await service.GetAsync(new ExceptionLogQuery { Account = "alice", PageSize = 10 });
        Assert.Equal("/ui", Assert.Single(byAccount.Result).Page);

        var byKeyword = await service.GetAsync(new ExceptionLogQuery { Keyword = "/api", PageSize = 10 });
        Assert.Equal("/api", Assert.Single(byKeyword.Result).Page);
    }

    [Fact]
    public async Task GetStackTraceAsync_MissingFile_ShouldReturnNullRatherThanThrow()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry());

        var row = Assert.Single(await fixture.ListAsync());
        File.Delete(fixture.FullPath(row.StackTraceFile!));

        Assert.Null(await service.GetStackTraceAsync(row.Id));
    }

    [Fact]
    public async Task GetStackTracesAsync_ShouldReturnEachStackAndNullForMissingOrUnknown()
    {
        await using var fixture = await ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(NewEntry(page: "/a", stackTrace: "stack-a"));
        await service.RecordAsync(NewEntry(page: "/b", stackTrace: "stack-b"));
        await service.RecordAsync(NewEntry(page: "/c", stackTrace: "stack-c"));

        var rows = await fixture.ListAsync();
        var a = rows.Single(x => x.Page == "/a");
        var b = rows.Single(x => x.Page == "/b");
        var c = rows.Single(x => x.Page == "/c");
        File.Delete(fixture.FullPath(c.StackTraceFile!));

        var result = await service.GetStackTracesAsync([a.Id, b.Id, c.Id, 999]);

        Assert.Contains("stack-a", result[a.Id]);
        Assert.Contains("stack-b", result[b.Id]);
        Assert.Null(result[c.Id]);
        Assert.Null(result[999]);
    }

    private static ExceptionLogEntry NewEntry(
        string? page = "/categories",
        string? operation = "Failed to create category. Name={CategoryName}",
        string message = "物件參照未設定",
        string source = ExceptionSources.Ui,
        string account = "support",
        string stackTrace = "stack-trace-content",
        DateTime? occurredAt = null,
        string? traceId = null,
        bool isCritical = false)
        => new()
        {
            TraceId = traceId,
            IsCritical = isCritical,
            ExceptionType = "System.NullReferenceException",
            Message = message,
            StackTrace = stackTrace,
            Source = source,
            Page = page,
            Operation = operation,
            LoggerName = "MeetingRecord.Business.Services.DataAccess.CategoryService",
            Account = account,
            UserId = 1,
            OccurredAt = occurredAt ?? DateTime.Now,
        };

    private sealed class ExceptionLogFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private ExceptionLogFixture(SqliteConnection connection, string exceptionPath)
        {
            this.connection = connection;
            ExceptionPath = exceptionPath;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public string ExceptionPath { get; }

        public string FullPath(string relativePath)
            => Path.Combine(ExceptionPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static async Task<ExceptionLogFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var exceptionPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exceptionPath);

            return new ExceptionLogFixture(connection, exceptionPath);
        }

        public ExceptionLogService CreateService()
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.ExceptionPath = ExceptionPath;

            var fileStore = new ExceptionStackFileStore(Options.Create(settings));

            return new ExceptionLogService(
                new TestDbContextFactory(connection).CreateDbContext(),
                mapper,
                loggerFactory.CreateLogger<ExceptionLogService>(),
                fileStore);
        }

        public async Task<List<ExceptionLog>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.ExceptionLog.AsNoTracking().ToListAsync();
        }

        public async Task SeedRowsAsync(int count)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            for (var index = 0; index < count; index++)
            {
                context.ExceptionLog.Add(new ExceptionLog
                {
                    Signature = $"seed-{index}",
                    ExceptionType = "System.Exception",
                    Message = $"seed-{index}",
                    Source = ExceptionSources.Unknown,
                    OccurrenceCount = 1,
                    FirstOccurredAt = DateTime.Now,
                    LastOccurredAt = DateTime.Now,
                });
            }

            await context.SaveChangesAsync();
        }

        public async Task MakeStaleAsync(string page, DateTime lastOccurredAt)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            var row = await context.ExceptionLog.FirstAsync(x => x.Page == page);
            row.LastOccurredAt = lastOccurredAt;
            await context.SaveChangesAsync();
        }

        /// <summary>刻意關閉連線，用來驗證「RecordAsync 不得拋出」。</summary>
        public async Task DisposeConnectionAsync() => await connection.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(ExceptionPath))
                {
                    Directory.Delete(ExceptionPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試殘留目錄不影響結果。
            }
        }
    }
}
