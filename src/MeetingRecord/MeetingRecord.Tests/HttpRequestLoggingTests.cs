using Microsoft.AspNetCore.Http;
using MeetingRecord.Web.Extensions;

namespace MeetingRecord.Tests;

/// <summary>1.1.49：請求日誌略過 WASM 框架檔與套件靜態檔（每次載入幾十個），API 與頁面照記。</summary>
public sealed class HttpRequestLoggingTests
{
    [Theory]
    [InlineData("/_framework/dotnet.native.wasm")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/AntDesign/css/ant-design-blazor.css")]
    public void StaticAssets_ShouldBeSkipped(string path)
        => Assert.True(ApplicationBuilderExtensions.IsStaticAssetPath(new PathString(path)));

    [Theory]
    [InlineData("/api/v1/session")]
    [InlineData("/meetings")]
    [InlineData("/")]
    [InlineData("/hubs/app/negotiate")]
    [InlineData("/_frameworkx/evil")]
    public void EverythingElse_ShouldStillBeLogged(string path)
        => Assert.False(ApplicationBuilderExtensions.IsStaticAssetPath(new PathString(path)));
}
