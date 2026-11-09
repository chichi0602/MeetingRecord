namespace MeetingRecord.E2ETests.Infrastructure;

/// <summary>
/// 端對端測試的 <c>[Fact]</c>：沒有設定 <c>MEETINGRECORD_E2E=1</c> 時自動 Skip。
/// CI 的 <c>dotnet test</c> 因此只會編譯這個專案、不會真的啟動網站與瀏覽器。
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!E2ESwitch.Enabled)
        {
            Skip = E2ESwitch.SkipReason;
        }
    }
}

/// <summary>端對端測試的 <c>[Theory]</c>，規則同 <see cref="E2EFactAttribute"/>。</summary>
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (!E2ESwitch.Enabled)
        {
            Skip = E2ESwitch.SkipReason;
        }
    }
}

internal static class E2ESwitch
{
    public static bool Enabled => Environment.GetEnvironmentVariable("MEETINGRECORD_E2E") == "1";

    public const string SkipReason = "端對端測試：設定 MEETINGRECORD_E2E=1 後執行（建議用 scripts/Run-E2E.ps1）。";
}
