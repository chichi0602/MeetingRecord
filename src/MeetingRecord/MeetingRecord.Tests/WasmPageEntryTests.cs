using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 開 WASM 頁的入口（1.1.58）：
/// 未登入時伺服器直接導到登入頁並記住原網址，不必先下載整個 WASM 才被導走；
/// 已登入時頁面先送出載入畫面（#app-loading），WASM 跑起來之前畫面不是一片空白。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed partial class WasmPageEntryTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    [Fact]
    public async Task Anonymous_WasmPage_ShouldRedirectToLoginWithReturnUrl()
    {
        using var client = NoRedirectClient();

        using var response = await client.GetAsync("/meetings?tab=1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Auths/Login?ReturnUrl=%2Fmeetings%3Ftab%3D1", PathAndQuery(response));
    }

    [Fact]
    public async Task Anonymous_Home_ShouldRedirectToLoginWithReturnUrl()
    {
        using var client = NoRedirectClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Auths/Login?ReturnUrl=%2F", PathAndQuery(response));
    }

    [Fact]
    public async Task Anonymous_LoginPage_ShouldRenderWithoutLoadingScreen_AndWarmUpWasm()
    {
        using var client = NoRedirectClient();

        using var response = await client.GetAsync("/Auths/Login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("id=\"app-loading\"", html);
        // 趁輸入帳密時在背景下載 WASM：登入頁放了一個瀏覽器端的空元件。
        Assert.Contains("MeetingRecord.Web.Client.Components.WasmWarmup", html);
    }

    [Fact]
    public async Task Anonymous_MissingStaticFile_ShouldStay404()
    {
        // 缺檔的 404 會重新執行到 /not-found（WASM 頁）；這時不能變成導向登入頁的 302。
        using var client = NoRedirectClient();

        using var response = await client.GetAsync("/images/does-not-exist-1158.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LoggedIn_WasmPage_ShouldRenderLoadingScreenWithSystemName()
    {
        using var client = NoRedirectClient();
        await TestCookieLogin.LoginAsync(client, "support", "support");
        var systemName = factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.SystemInformation.SystemName;

        using var response = await client.GetAsync("/meetings");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"app-loading\"", html);
        Assert.Contains("系統載入中", html);
        Assert.Contains(systemName, html);
        Assert.Matches(AppLoadingScriptPattern(), html);
    }

    [Fact]
    public async Task Login_WithReturnUrl_ShouldGoBackToOriginalPage()
    {
        using var client = NoRedirectClient();

        using var response = await TestCookieLogin.LoginAsync(client, "support", "support", "/Auths/Login?ReturnUrl=%2Ftodos%3Fx%3D1");

        Assert.Equal("/todos?x=1", response.Headers.Location?.OriginalString);
    }

    /// <summary>NavigationManager 導向時給的是絕對網址；只比對路徑與查詢字串。</summary>
    private static string? PathAndQuery(HttpResponseMessage response)
        => response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString
            : null;

    private HttpClient NoRedirectClient()
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [GeneratedRegex(@"<script src=""[^""]*js/app-loading[^""]*\.js""></script>")]
    private static partial Regex AppLoadingScriptPattern();
}
