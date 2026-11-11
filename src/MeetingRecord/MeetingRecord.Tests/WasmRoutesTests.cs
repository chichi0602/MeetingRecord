using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.39：WASM 頁的判斷要忽略查詢字串與錨點。Router 的 <c>NavigationContext.Path</c> 帶著查詢字串，
/// 以前 <c>/ai-call-logs?callId=…</c> 被當成 Server 頁、WASM 端強制整頁重載，伺服器又送回 WASM 頁，無限重載。
/// </summary>
public sealed class WasmRoutesTests
{
    [Theory]
    [InlineData("/ai-call-logs")]
    [InlineData("/ai-call-logs/")]
    [InlineData("/ai-call-logs?callId=94300385-c162-4b1a-b193-897b1fe436a8")]
    [InlineData("/AI-CALL-LOGS?callId=x")]
    [InlineData("/meetings#top")]
    [InlineData("/todos/?a=1#b")]
    public void IsWasm_ShouldIgnoreQueryAndFragment(string path)
        => Assert.True(WasmRoutes.IsWasm(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/not-found")]
    [InlineData("/not-found?from=/ai-call-logs")]
    [InlineData("/ai-call-logs-x")]
    public void IsWasm_ShouldRejectOtherPaths(string? path)
        => Assert.False(WasmRoutes.IsWasm(path));
}
