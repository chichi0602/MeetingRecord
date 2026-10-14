using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.Web.Diagnostics;
using NLog.Config;
using NLog.Targets;
using NLogLevel = NLog.LogLevel;
using NLogManager = NLog.LogManager;

namespace MeetingRecord.Tests;

/// <summary>
/// 執行期日誌等級狀態的測試。
///
/// 這些測試會動到行程層級的 NLog 設定，因此以 Collection 序列化執行，並在每個測試
/// 結束後還原原本的設定，避免污染其他測試（例如日誌查詢那組）。
/// </summary>
[Collection(nameof(NLogConfigurationCollection))]
public sealed class LogLevelRuntimeStateTests : IDisposable
{
    private readonly LoggingConfiguration? originalConfiguration;
    private readonly List<LogLevelRuntimeState> created = [];

    public LogLevelRuntimeStateTests()
    {
        originalConfiguration = NLogManager.Configuration;
    }

    public void Dispose()
    {
        // 必須先解除訂閱再還原設定，否則還原動作會觸發這些實例的 ConfigurationChanged。
        foreach (var state in created)
        {
            state.Dispose();
        }

        NLogManager.Configuration = originalConfiguration;
    }

    private LogLevelRuntimeState Track(LogLevelRuntimeState state)
    {
        created.Add(state);
        return state;
    }

    /// <summary>
    /// 建出與 nlog.config 同形狀的設定：前面是 final 的框架規則，最後是萬用的應用程式規則。
    /// </summary>
    private static LoggingConfiguration BuildConfiguration(NLogLevel applicationMinLevel)
    {
        var configuration = new LoggingConfiguration();
        var target = new MemoryTarget("mem");
        configuration.AddTarget(target);

        var frameworkRule = new LoggingRule("Microsoft.*", NLogLevel.Warn, target) { Final = true };
        configuration.LoggingRules.Add(frameworkRule);

        // 對應 nlog.config 的「強制排除」規則：涵蓋所有等級、final="true"、且**沒有 target**。
        // 這三個條件缺一不可 —— 少了它，Microsoft.* 的 Debug 訊息會因為不符合上面 Warn 規則
        // 的等級範圍而繼續往下比對，最後被萬用規則收走（實測確認過）。
        var suppressRule = new LoggingRule { LoggerNamePattern = "Microsoft.*", Final = true };
        suppressRule.EnableLoggingForLevels(NLogLevel.Trace, NLogLevel.Fatal);
        configuration.LoggingRules.Add(suppressRule);

        var applicationRule = new LoggingRule("*", applicationMinLevel, target);
        configuration.LoggingRules.Add(applicationRule);

        configuration.Variables["BasePath"] = @"C:\temp\Logs\MeetingRecord.Web";
        configuration.Variables["LogFilenamePrefix"] = "MeetingRecord.Web-logfile";

        return configuration;
    }

    private LogLevelRuntimeState CreateInitializedState(NLogLevel applicationMinLevel)
    {
        NLogManager.Configuration = BuildConfiguration(applicationMinLevel);
        var state = new LogLevelRuntimeState(NullLogger<LogLevelRuntimeState>.Instance);
        state.Initialize();
        return Track(state);
    }

    private static LoggingRule ApplicationRule()
        => NLogManager.Configuration!.LoggingRules.First(rule => rule.LoggerNamePattern == "*");

    private static LogLevelRank ApplicationMinimum()
        => (LogLevelRank)ApplicationRule().Levels.Min(level => level.Ordinal);

    [Fact]
    public void Initialize_ShouldCaptureSystemDefaultFromConfiguration()
    {
        var state = CreateInitializedState(NLogLevel.Info);

        Assert.True(state.IsAvailable);
        Assert.Equal(LogLevelRank.Info, state.SystemDefaultLevel);
        Assert.Null(state.OverrideLevel);
        Assert.Equal(LogLevelRank.Info, state.EffectiveLevel);
    }

    [Fact]
    public void Initialize_ShouldReadWhateverTheFileSays()
    {
        // 系統預設不該被硬編碼成 Info —— 有人改了 nlog.config 就要跟著變。
        var state = CreateInitializedState(NLogLevel.Warn);

        Assert.Equal(LogLevelRank.Warn, state.SystemDefaultLevel);
    }

    [Fact]
    public void Apply_ShouldChangeTheApplicationRuleMinimumLevel()
    {
        var state = CreateInitializedState(NLogLevel.Info);

        Assert.True(state.Apply(LogLevelRank.Debug));

        Assert.Equal(LogLevelRank.Debug, ApplicationMinimum());
        Assert.Equal(LogLevelRank.Debug, state.EffectiveLevel);
        Assert.Equal(LogLevelRank.Debug, state.OverrideLevel);
        // 系統預設不因套用而改變。
        Assert.Equal(LogLevelRank.Info, state.SystemDefaultLevel);
    }

    [Fact]
    public void Apply_ShouldNotTouchTheFrameworkRule()
    {
        // 調低等級只該影響應用程式日誌；框架規則是 final，必須原封不動。
        var state = CreateInitializedState(NLogLevel.Info);
        state.Apply(LogLevelRank.Trace);

        var frameworkRule = NLogManager.Configuration!.LoggingRules
            .First(rule => rule.LoggerNamePattern == "Microsoft.*");

        Assert.Equal((int)LogLevelRank.Warn, frameworkRule.Levels.Min(level => level.Ordinal));
    }

    [Fact]
    public void RestoreDefault_ShouldReturnToTheCapturedDefault()
    {
        var state = CreateInitializedState(NLogLevel.Info);
        state.Apply(LogLevelRank.Trace);

        Assert.True(state.RestoreDefault());

        Assert.Equal(LogLevelRank.Info, ApplicationMinimum());
        Assert.Null(state.OverrideLevel);
        Assert.Equal(LogLevelRank.Info, state.EffectiveLevel);
    }

    [Fact]
    public void RestoreDefault_WithoutOverride_ShouldReturnFalse()
    {
        var state = CreateInitializedState(NLogLevel.Info);

        Assert.False(state.RestoreDefault());
    }

    [Theory]
    [InlineData(LogLevelRank.Any)]
    [InlineData(LogLevelRank.Unknown)]
    public void Apply_WithNonRealLevel_ShouldBeRejected(LogLevelRank level)
    {
        // Any(-1) 與 Unknown(99) 不在 NLog 的序位範圍內，直接丟給 FromOrdinal 會例外。
        var state = CreateInitializedState(NLogLevel.Info);

        Assert.False(state.Apply(level));
        Assert.Equal(LogLevelRank.Info, ApplicationMinimum());
    }

    [Fact]
    public void Initialize_WithoutApplicationRule_ShouldDegradeGracefully()
    {
        var configuration = new LoggingConfiguration();
        var target = new MemoryTarget("mem");
        configuration.AddTarget(target);
        configuration.LoggingRules.Add(new LoggingRule("Microsoft.*", NLogLevel.Warn, target));
        NLogManager.Configuration = configuration;

        var state = Track(new LogLevelRuntimeState(NullLogger<LogLevelRuntimeState>.Instance));
        state.Initialize();

        Assert.False(state.IsAvailable);
        Assert.False(state.Apply(LogLevelRank.Debug));
    }

    [Fact]
    public void LoweringLevel_AgainstRealConfig_ShouldOnlyAffectApplicationLoggers()
    {
        // 畫面上寫著「本設定只影響應用程式日誌；框架日誌另由 nlog.config 的專屬規則控制」。
        // 這條測試直接拿專案真正的 nlog.config 驗證那句話，而不是拿合成設定 ——
        // 合成設定重現不了 final 規則的實際行為（第一版就是這樣誤判的）。
        var configuration = new XmlLoggingConfiguration(FindRealNlogConfig());

        var memory = new MemoryTarget("mem") { Layout = "${level:uppercase=true}|${logger}|${message}" };
        configuration.AddTarget(memory);

        // 只把原本就有 target 的規則改指向記憶體 target；
        // 原本無 target 的是「強制排除」規則，必須維持無 target 才會繼續當抑制器。
        foreach (var rule in configuration.LoggingRules)
        {
            var hadTargets = rule.Targets.Count > 0;
            rule.Targets.Clear();
            if (hadTargets)
            {
                rule.Targets.Add(memory);
            }
        }

        NLogManager.Configuration = configuration;
        var runtimeState = Track(new LogLevelRuntimeState(NullLogger<LogLevelRuntimeState>.Instance));
        runtimeState.Initialize();

        Assert.True(runtimeState.Apply(LogLevelRank.Debug));
        memory.Logs.Clear();

        NLogManager.GetLogger("MeetingRecord.Web.SomeService").Debug("APP-DEBUG");
        NLogManager.GetLogger("Microsoft.EntityFrameworkCore.Query").Debug("EF-DEBUG");
        NLogManager.GetLogger("Microsoft.AspNetCore.Something").Debug("MS-DEBUG");

        Assert.Contains(memory.Logs, line => line.Contains("APP-DEBUG"));
        Assert.DoesNotContain(memory.Logs, line => line.Contains("EF-DEBUG"));
        Assert.DoesNotContain(memory.Logs, line => line.Contains("MS-DEBUG"));
    }

    private static string FindRealNlogConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "MeetingRecord", "MeetingRecord.Web", "nlog.config");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 MeetingRecord.Web/nlog.config。");
    }

    [Theory]
    [InlineData(LogLevelRank.Trace, 0)]
    [InlineData(LogLevelRank.Debug, 1)]
    [InlineData(LogLevelRank.Info, 2)]
    [InlineData(LogLevelRank.Warn, 3)]
    [InlineData(LogLevelRank.Error, 4)]
    [InlineData(LogLevelRank.Fatal, 5)]
    public void LogLevelRank_ShouldRoundTripWithNLogOrdinal(LogLevelRank rank, int expectedOrdinal)
    {
        // 這個對應關係是整個橋接的前提：序位一致才能免去字串剖析。
        var nlogLevel = NLogLevel.FromOrdinal((int)rank);

        Assert.Equal(expectedOrdinal, nlogLevel.Ordinal);
        Assert.Equal(rank, (LogLevelRank)nlogLevel.Ordinal);
        Assert.Equal(nlogLevel.Name.ToUpperInvariant(), LogLevelRankHelper.ToLevelText(rank));
    }
}

/// <summary>
/// 動到行程層級 NLog 設定的測試共用此 Collection，確保不會平行執行而互相干擾。
/// </summary>
[CollectionDefinition(nameof(NLogConfigurationCollection), DisableParallelization = true)]
public sealed class NLogConfigurationCollection
{
}
