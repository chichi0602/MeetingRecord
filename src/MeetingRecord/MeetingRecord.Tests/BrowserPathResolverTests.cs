using MeetingRecord.Business.Services.Export;

namespace MeetingRecord.Tests;

public sealed class BrowserPathResolverTests
{
    [Fact]
    public void Resolve_WithConfiguredExistingPath_ShouldUseIt()
    {
        var resolved = BrowserPathResolver.Resolve(@"C:\custom\browser.exe", _ => true);

        Assert.Equal(@"C:\custom\browser.exe", resolved);
    }

    [Fact]
    public void Resolve_WithConfiguredMissingPath_ShouldReturnNullInsteadOfProbing()
    {
        // 設定值指到不存在的檔案時要回 null 讓使用者看到錯誤，
        // 不能靜默改用自動偵測到的其他瀏覽器。
        var resolved = BrowserPathResolver.Resolve(@"C:\nowhere\browser.exe", path => !path.Contains("nowhere"));

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_WithBlankConfiguration_ShouldPickFirstAvailableWellKnownPath()
    {
        var expected = BrowserPathResolver.WellKnownPaths[1];

        var resolved = BrowserPathResolver.Resolve(string.Empty, path => path == expected);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_WithBlankConfigurationAndNoBrowser_ShouldReturnNull()
    {
        Assert.Null(BrowserPathResolver.Resolve(null, _ => false));
    }

    [Fact]
    public void WellKnownPaths_ShouldPreferEdge()
    {
        // Windows 一定有 Edge，優先用它可以讓絕大多數部署零設定就能運作。
        Assert.Contains("msedge.exe", BrowserPathResolver.WellKnownPaths[0]);
    }
}
