using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Tests;

/// <summary>
/// 管線自我監控的計數，與系統健康監控「例外紀錄管線」的判斷（0.4.123，移植自 NET10-Blazor-Starter，拿掉 Email 告警的案例）。
/// </summary>
public sealed class LoggingPipelineHealthTests
{
    [Fact]
    public void Monitor_ShouldAccumulateCounters()
    {
        var monitor = new LoggingPipelineMonitor();
        var now = new DateTimeOffset(2026, 10, 14, 1, 0, 0, TimeSpan.Zero);

        monitor.RecordWriteFailure(now);
        monitor.RecordWriteFailure(now.AddMinutes(1));
        monitor.RecordClientErrorDropped();
        monitor.RecordNLogInternalError(now);

        var snapshot = monitor.Snapshot();
        Assert.Equal(2, snapshot.WriteFailures);
        Assert.Equal(now.AddMinutes(1), snapshot.LastWriteFailureAt);
        Assert.Equal(1, snapshot.ClientErrorsDropped);
        Assert.Equal(1, snapshot.NLogInternalErrors);
        Assert.Equal(now, snapshot.LastNLogInternalErrorAt);
    }

    [Fact]
    public void Evaluate_AllClear_ShouldBeHealthy()
    {
        var verdict = SystemHealthChecks.EvaluateExceptionPipeline(Empty, 0, 1000, 0, 50);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Null(verdict.FailureMessage);
        Assert.StartsWith("本次啟動以來", verdict.Evidence);
    }

    [Fact]
    public void Evaluate_ClientErrorDropsOnly_ShouldStayHealthy()
    {
        // 瀏覽器回報被限流擋下是照設計運作，不算管線出問題。
        var verdict = SystemHealthChecks.EvaluateExceptionPipeline(Empty with { ClientErrorsDropped = 30 }, 0, 1000, 0, 50);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Contains("限流丟棄 30", verdict.Evidence);
    }

    [Theory]
    [MemberData(nameof(DegradedCases))]
    public void Evaluate_AnyPipelineProblem_ShouldDegrade(LoggingPipelineSnapshot snapshot, int queueLength, long dropped, double? diskGb)
    {
        var verdict = SystemHealthChecks.EvaluateExceptionPipeline(snapshot, queueLength, 1000, dropped, diskGb);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
        Assert.False(string.IsNullOrWhiteSpace(verdict.FailureMessage));
    }

    public static TheoryData<LoggingPipelineSnapshot, int, long, double?> DegradedCases() => new()
    {
        { Empty with { WriteFailures = 1 }, 0, 0, 50 },
        { Empty with { NLogInternalErrors = 1 }, 0, 0, 50 },
        { Empty, 800, 0, 50 },
        { Empty, 0, 3, 50 },
        { Empty, 0, 0, 0.5 },
    };

    [Theory]
    [InlineData(1000, 50.0)]
    [InlineData(0, 0.1)]
    public void Evaluate_QueueFullOrDiskAlmostFull_ShouldBeUnhealthy(int queueLength, double diskGb)
    {
        var verdict = SystemHealthChecks.EvaluateExceptionPipeline(Empty, queueLength, 1000, 0, diskGb);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Fact]
    public void Evaluate_DiskUnknown_ShouldNotAffectStatus()
    {
        var verdict = SystemHealthChecks.EvaluateExceptionPipeline(Empty, 0, 1000, 0, null);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Contains("未設定或無法讀取", verdict.Evidence);
    }

    private static LoggingPipelineSnapshot Empty => new(0, null, 0, 0, null);
}
