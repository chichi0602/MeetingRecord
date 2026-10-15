using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Auths;

namespace MeetingRecord.Tests;

/// <summary>登出頁要寫哪一種稽核（1.0.33，<c>Logout.BuildAudit</c>）。</summary>
public sealed class LogoutAuditTests
{
    [Theory]
    [InlineData(LogoutReasons.InvalidClaim)]
    [InlineData(LogoutReasons.Deleted)]
    [InlineData(LogoutReasons.Disabled)]
    [InlineData(LogoutReasons.NoRole)]
    [InlineData(LogoutReasons.PermissionError)]
    public void KnownReason_ShouldBeForcedLogout(string reason)
    {
        var audit = Logout.BuildAudit(reason, 3, "alice");

        Assert.Equal((AuditActions.LogoutForced, $"reason={reason}"), audit);
    }

    /// <summary>Sid 無效時認不出是誰，被系統登出照樣要記。</summary>
    [Fact]
    public void KnownReason_WithoutIdentity_ShouldStillBeRecorded()
    {
        Assert.Equal(AuditActions.LogoutForced, Logout.BuildAudit(LogoutReasons.InvalidClaim, null, null)?.Action);
    }

    [Fact]
    public void NoReason_WithIdentity_ShouldBeManualLogout()
    {
        Assert.Equal((AuditActions.LogoutManual, "reason=user"), Logout.BuildAudit(null, 3, "alice"));
    }

    [Fact]
    public void UnknownReason_ShouldBeTreatedAsManualLogout()
    {
        Assert.Equal(AuditActions.LogoutManual, Logout.BuildAudit("<script>", 3, "alice")?.Action);
    }

    [Fact]
    public void NoReason_WithoutIdentity_ShouldNotWrite()
    {
        Assert.Null(Logout.BuildAudit(null, null, null));
        Assert.Null(Logout.BuildAudit("whatever", null, null));
    }
}
