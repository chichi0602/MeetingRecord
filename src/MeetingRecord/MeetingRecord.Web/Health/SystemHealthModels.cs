namespace MeetingRecord.Web.Health;

public enum SystemHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

public enum SystemHealthLight
{
    Green,
    Yellow,
    Red
}

public sealed class SystemHealthReport
{
    public DateTimeOffset CheckedAt { get; init; }
    public int Score { get; init; }
    public SystemHealthStatus Status { get; init; }
    public SystemHealthLight Light { get; init; }
    public IReadOnlyList<SystemHealthItem> Items { get; init; } = [];
    public HealthLogTail LogTail { get; init; } = HealthLogTail.Empty;
}

public sealed class SystemHealthItem
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public int Weight { get; init; }
    public SystemHealthStatus Status { get; init; }
    public SystemHealthLight Light { get; init; }
    public required string Evidence { get; init; }
    public string? FailureMessage { get; init; }
}

public sealed class HealthLogTail
{
    public static readonly HealthLogTail Empty = new()
    {
        FilePath = string.Empty,
        Lines = [],
        Status = SystemHealthStatus.Degraded,
        Message = "尚未讀取日誌。"
    };

    public required string FilePath { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public SystemHealthStatus Status { get; init; }
    public required string Message { get; init; }
}
