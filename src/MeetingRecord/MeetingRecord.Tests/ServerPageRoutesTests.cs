using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.45：仍由伺服器靜態算繪的頁面只有 <c>/Auths/*</c> 與 <c>/Error</c>，其餘都是 WASM 頁。
/// 判斷要忽略查詢字串與錨點（1.1.39：Router 的 <c>NavigationContext.Path</c> 帶著查詢字串，以前因此無限重載）。
/// </summary>
public sealed class ServerPageRoutesTests
{
    [Theory]
    [InlineData("/Auths/Login")]
    [InlineData("/auths/login?returnUrl=%2Fmeetings")]
    [InlineData("/Auths/Logout?reason=disabled")]
    [InlineData("/Auths/Pending")]
    [InlineData("/Error")]
    [InlineData("/error#top")]
    public void IsServerPage_ShouldMatchLoginLogoutAndError(string path)
        => Assert.True(ServerPageRoutes.IsServerPage(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/not-found")]
    [InlineData("/meetings")]
    [InlineData("/ai-call-logs?callId=94300385-c162-4b1a-b193-897b1fe436a8")]
    [InlineData("/Auths")]
    [InlineData("/Errors")]
    [InlineData("/counter")]
    public void IsServerPage_ShouldTreatEverythingElseAsWasm(string? path)
        => Assert.False(ServerPageRoutes.IsServerPage(path));
}
