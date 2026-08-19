namespace MeetingRecord.Web.Health;

public sealed class SystemStartupState
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
}
