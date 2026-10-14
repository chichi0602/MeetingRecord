using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 例外捕捉的進入點（ILoggerProvider）。
///
/// 這支測試守住兩件容易出事的事：
/// 1. <b>防遞迴</b> —— 寫入器自己記的錯誤若被收回管線，就會「失敗 → 記錄 → 再失敗」無限循環。
/// 2. <b>取訊息樣板而非算好的訊息</b> —— 取錯不會有任何錯誤訊息，只會在正式環境慢慢把列數撐爆。
/// </summary>
public sealed class ExceptionLogProviderTests
{
    [Fact]
    public void Log_ErrorWithException_ShouldEnqueueEntry()
    {
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("MeetingRecord.Business.Services.DataAccess.CategoryService");

        logger.LogError(new InvalidOperationException("boom"), "Failed to create category. Name={CategoryName}", "技術文件");

        var entry = Assert.Single(harness.DrainEntries());
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Equal("boom", entry.Message);
        Assert.Equal("MeetingRecord.Business.Services.DataAccess.CategoryService", entry.LoggerName);
    }

    [Fact]
    public void Log_ShouldCaptureMessageTemplateNotFormattedMessage()
    {
        // ⚠️ 整個設計最關鍵的一條斷言。
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("MeetingRecord.Business.Services.DataAccess.CategoryService");

        logger.LogError(new Exception("boom"), "Failed to create category. Name={CategoryName}", "技術文件");

        var entry = Assert.Single(harness.DrainEntries());
        Assert.Equal("Failed to create category. Name={CategoryName}", entry.Operation);
        Assert.DoesNotContain("技術文件", entry.Operation);
    }

    [Fact]
    public void Log_ShouldReadAmbientContext()
    {
        using var harness = new ProviderHarness();
        harness.Accessor.Set(new ExceptionContext(ExceptionSources.WebApi, "/api/v1/projects", "alice", 42));
        var logger = harness.CreateLogger("Some.Controller");

        logger.LogError(new Exception("boom"), "Something failed.");

        var entry = Assert.Single(harness.DrainEntries());
        Assert.Equal(ExceptionSources.WebApi, entry.Source);
        Assert.Equal("/api/v1/projects", entry.Page);
        Assert.Equal("alice", entry.Account);
        Assert.Equal(42, entry.UserId);
    }

    [Fact]
    public void Log_NoAmbientContext_ShouldFallBackToUnknown()
    {
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        logger.LogError(new Exception("boom"), "Something failed.");

        Assert.Equal(ExceptionSources.Unknown, Assert.Single(harness.DrainEntries()).Source);
    }

    [Fact]
    public void Log_WarningLevel_ShouldNotBeCaptured()
    {
        // Warning 多半是「已處理、降級可用」，收進來只是噪音。
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        logger.LogWarning(new Exception("boom"), "Degraded but handled.");

        Assert.Empty(harness.DrainEntries());
    }

    [Fact]
    public void Log_ErrorWithoutException_ShouldNotBeCaptured()
    {
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        logger.LogError("Something failed but there is no exception object.");

        Assert.Empty(harness.DrainEntries());
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public void Log_CancellationExceptions_ShouldNotBeCaptured(Type exceptionType)
    {
        // 使用者取消、正常關機屬「刻意忽略的情況」，不應出現在本頁。
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        logger.LogError(exception, "User cancelled.");

        Assert.Empty(harness.DrainEntries());
    }

    [Theory]
    [InlineData("MeetingRecord.Web.Diagnostics.ExceptionLogWriter")]
    [InlineData("MeetingRecord.Business.Services.DataAccess.ExceptionLogService")]
    [InlineData("MeetingRecord.Business.Services.Other.ExceptionStackFileStore")]
    public void Log_OwnSubsystemLoggers_ShouldNotBeCaptured(string categoryName)
    {
        // 防遞迴第一道防線。
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger(categoryName);

        logger.LogError(new Exception("boom"), "Writer failed.");

        Assert.Empty(harness.DrainEntries());
    }

    [Fact]
    public void Log_WhileSuppressed_ShouldNotBeCaptured()
    {
        // 防遞迴第二道防線：寫入器執行期間全程開啟。
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        using (harness.Accessor.Suppress())
        {
            logger.LogError(new Exception("boom"), "Failure inside the writer.");
        }

        Assert.Empty(harness.DrainEntries());
    }

    [Fact]
    public void Log_AfterSuppressionScopeEnds_ShouldCaptureAgain()
    {
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        using (harness.Accessor.Suppress())
        {
            logger.LogError(new Exception("suppressed"), "Inside.");
        }

        logger.LogError(new Exception("captured"), "Outside.");

        Assert.Equal("captured", Assert.Single(harness.DrainEntries()).Message);
    }

    [Fact]
    public void Log_WhenChannelIsFull_ShouldDropWithoutThrowing()
    {
        // 寧可漏記，不可拖垮主流程 —— 與 nlog.config 的 overflowAction="Discard" 同一種取捨。
        using var harness = new ProviderHarness(capacity: 2);
        var logger = harness.CreateLogger("Some.Service");

        // DroppedCount 是行程全域的 static，其他測試也可能累加，只能比對增量。
        var droppedBefore = ExceptionLogProvider.DroppedCount;

        var exception = Record.Exception(() =>
        {
            for (var index = 0; index < 50; index++)
            {
                logger.LogError(new Exception($"boom-{index}"), "Failure {Index}.", index);
            }
        });

        Assert.Null(exception);
        Assert.Equal(2, harness.DrainEntries().Count);

        // 0.9.77 之前 Channel 是 DropWrite，TryWrite 永遠回 true，這裡會是 0。
        Assert.True(ExceptionLogProvider.DroppedCount - droppedBefore >= 48);
    }

    [Fact]
    public void Log_SameExceptionInstanceTwice_ShouldCaptureOnlyOnce()
    {
        // 例如 UseHttpRequestLogging 記完重拋，框架的 ExceptionHandlerMiddleware 再記一次：
        // 樣板不同 → 兩個簽章 → 同一次發生變兩列。以實例去重，只收第一次。
        using var harness = new ProviderHarness();
        var inner = harness.CreateLogger("MeetingRecord.Web.Program");
        var framework = harness.CreateLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        var exception = new InvalidOperationException("boom");

        inner.LogError(exception, "HTTP {Method} {Path} failed after {ElapsedMilliseconds} ms", "GET", "/x", 1);
        framework.LogError(exception, "An unhandled exception has occurred while executing the request.");

        var entry = Assert.Single(harness.DrainEntries());
        Assert.Equal("MeetingRecord.Web.Program", entry.LoggerName);
    }

    [Fact]
    public void Log_DifferentExceptionInstances_ShouldEachBeCaptured()
    {
        using var harness = new ProviderHarness();
        var logger = harness.CreateLogger("Some.Service");

        logger.LogError(new InvalidOperationException("boom"), "Something failed.");
        logger.LogError(new InvalidOperationException("boom"), "Something failed.");

        Assert.Equal(2, harness.DrainEntries().Count);
    }

    private sealed class ProviderHarness : IDisposable
    {
        private readonly Channel<ExceptionLogEntry> channel;
        private readonly ExceptionLogProvider provider;

        public ProviderHarness(int capacity = 100)
        {
            channel = Channel.CreateBounded<ExceptionLogEntry>(
                new BoundedChannelOptions(capacity)
                {
                    // 與正式環境相同（ServiceCollectionExtensions）：Wait ＋ TryWrite，滿載時才回得出 false。
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });

            Accessor = new ExceptionContextAccessor();
            provider = new ExceptionLogProvider(channel.Writer, Accessor);
        }

        public ExceptionContextAccessor Accessor { get; }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public List<ExceptionLogEntry> DrainEntries()
        {
            var entries = new List<ExceptionLogEntry>();
            while (channel.Reader.TryRead(out var entry))
            {
                entries.Add(entry);
            }

            return entries;
        }

        public void Dispose()
        {
            Accessor.Clear();
            provider.Dispose();
        }
    }
}
