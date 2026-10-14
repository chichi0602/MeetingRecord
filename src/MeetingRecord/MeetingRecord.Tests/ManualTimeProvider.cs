namespace MeetingRecord.Tests;

/// <summary>
/// 可手動推進的假時鐘（瀏覽器回報頻率限制的測試用）。
/// 本地時區固定為 UTC+8，讓「本地時間」與「UTC」兩種門檻的差異在測試裡看得出來。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        this.utcNow = utcNow;
    }

    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}
