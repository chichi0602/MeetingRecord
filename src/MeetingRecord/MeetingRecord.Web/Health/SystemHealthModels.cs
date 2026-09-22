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

    /// <summary>今日日誌整份檔案中 ERROR／FATAL 的筆數（0.4.93，不只最後 100 行）。</summary>
    public int ErrorCount { get; init; }
    public SystemHealthStatus Status { get; init; }
    public required string Message { get; init; }
}
