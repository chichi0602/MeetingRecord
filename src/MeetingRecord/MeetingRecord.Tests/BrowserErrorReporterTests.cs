using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>瀏覽器端錯誤回報（1.1.47 起經 API、依帳號限流）。前端送來的內容一律不信任。</summary>
public sealed class BrowserErrorReporterTests : IDisposable
{
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
    private readonly ExceptionContextAccessor accessor = new();
    private readonly LoggingPipelineMonitor monitor = new();
    private readonly CapturingLogger logger;

    public BrowserErrorReporterTests()
    {
        logger = new CapturingLogger(accessor);
    }

    [Fact]
    public void Report_ShouldLogErrorWithBrowserSourceAndUser()
    {
        var reporter = CreateReporter();

        Assert.True(reporter.Report(7, "alice", "error", "x is not defined", "at foo (app.js:1:2)", "https://site/app.js:1:2", "/projects?token=secret#top"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        var exception = Assert.IsType<BrowserScriptException>(entry.Exception);
        Assert.Equal("x is not defined", exception.Message);
        Assert.Contains("at foo (app.js:1:2)", exception.ToString());
        Assert.Equal(ExceptionSources.Browser, entry.Context?.Source);
        Assert.Equal("/projects", entry.Context?.Page);   // 查詢字串與片段可能含 token，不得進紀錄
        Assert.Equal("alice", entry.Context?.Account);
        Assert.Equal(7, entry.Context?.UserId);
        Assert.Null(accessor.Current);                      // 情境用完要還原
    }

    [Fact]
    public void Report_OverRateLimit_ShouldDropAndCount()
    {
        var reporter = CreateReporter(new ClientErrorReportingSettings { MaxPerCircuitPerMinute = 3 });

        var results = Enumerable.Range(0, 5).Select(index => reporter.Report(7, "alice", "error", $"e{index}", null, null, "/")).ToList();

        Assert.Equal([true, true, true, false, false], results);
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(2, monitor.Snapshot().ClientErrorsDropped);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(reporter.Report(7, "alice", "error", "after window", null, null, "/"));
    }

    /// <summary>1.1.47：上限以帳號計——一個人洗版不會擋到別人的回報。</summary>
    [Fact]
    public void Report_RateLimit_ShouldBePerAccount()
    {
        var reporter = CreateReporter(new ClientErrorReportingSettings { MaxPerCircuitPerMinute = 2 });

        Assert.True(reporter.Report(7, "alice", "error", "a1", null, null, "/"));
        Assert.True(reporter.Report(7, "alice", "error", "a2", null, null, "/"));
        Assert.False(reporter.Report(7, "alice", "error", "a3", null, null, "/"));
        Assert.True(reporter.Report(8, "bob", "error", "b1", null, null, "/"));
        Assert.Equal("bob", logger.Entries[^1].Context?.Account);
    }

    [Fact]
    public void Report_WhenDisabled_ShouldIgnore()
    {
        var reporter = CreateReporter(new ClientErrorReportingSettings { Enabled = false });

        Assert.False(reporter.Report(7, "alice", "error", "boom", null, null, "/"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Report_ShouldTruncateOversizedInput()
    {
        var reporter = CreateReporter();

        reporter.Report(7, "alice", "weird-kind", new string('m', 5000), new string('s', 9000), null, "/" + new string('p', 500));

        var entry = Assert.Single(logger.Entries);
        var exception = Assert.IsType<BrowserScriptException>(entry.Exception);
        Assert.Equal(1000, exception.Message.Length);
        Assert.Equal(4000, exception.ScriptStack.Length);
        Assert.Equal("error", exception.Kind);              // 不認得的種類一律當 error
        Assert.Equal(200, entry.Context!.Page!.Length);
    }

    [Theory]
    [InlineData("unhandledrejection")]
    [InlineData("dotnet")]
    public void Report_KnownKinds_ShouldBeKept(string kind)
    {
        var reporter = CreateReporter();

        Assert.True(reporter.Report(7, "alice", kind, "boom", null, null, "/dashboard"));

        var exception = Assert.IsType<BrowserScriptException>(Assert.Single(logger.Entries).Exception);
        Assert.Equal(kind, exception.Kind);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("projects", "/projects")]
    [InlineData("/logs?keyword=abc", "/logs")]
    [InlineData("/a#b", "/a")]
    public void NormalizePath_ShouldKeepOnlyThePath(string? input, string expected)
    {
        Assert.Equal(expected, BrowserErrorReporter.NormalizePath(input));
    }

    private BrowserErrorReporter CreateReporter(ClientErrorReportingSettings? settings = null)
        => new(logger, accessor, new StaticMonitor(settings ?? new ClientErrorReportingSettings()), monitor, clock);

    public void Dispose() => accessor.Clear();

    private sealed class CapturingLogger(ExceptionContextAccessor accessor) : ILogger<BrowserErrorReporter>
    {
        public List<(LogLevel Level, Exception? Exception, ExceptionContext? Context)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, accessor.Current));
    }

    private sealed class StaticMonitor(ClientErrorReportingSettings value) : IOptionsMonitor<ClientErrorReportingSettings>
    {
        public ClientErrorReportingSettings CurrentValue { get; } = value;

        public ClientErrorReportingSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ClientErrorReportingSettings, string?> listener) => null;
    }
}
