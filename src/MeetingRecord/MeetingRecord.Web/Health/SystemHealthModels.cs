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
}

public static class SystemHealthGroups
{
    public const string Infrastructure = "基礎設施";
    public const string Features = "本系統功能";
}

public sealed class SystemHealthItem
{
    public required string Name { get; init; }
    public required string Category { get; init; }

    /// <summary>頁面上的分組（0.4.93）：<see cref="SystemHealthGroups"/>。</summary>
    public string Group { get; init; } = SystemHealthGroups.Infrastructure;
    public int Weight { get; init; }
    public SystemHealthStatus Status { get; init; }
    public SystemHealthLight Light { get; init; }
    public required string Evidence { get; init; }
    public string? FailureMessage { get; init; }
}
