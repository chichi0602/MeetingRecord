using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>
/// Web API 的驗證（1.1.0）：「帶 Bearer 走 JWT，否則走登入 Cookie」，WASM 前端用 Cookie 打 API。
/// 這裡用真的網頁登入表單取得 Cookie（不偽造 claims），驗證 Cookie 路徑的驗證、CSRF 防偽標頭與 Session API。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class CookieApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "cookie-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CookieLogin_ShouldReachProtectedCrudApi()
    {
        using var client = await CookieClientAsync("support", "support");

        using var response = await PostSearchAsync(client, withClientHeader: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CookieWrite_WithoutClientHeader_ShouldBeRejectedAsCsrf()
    {
        using var client = await CookieClientAsync("support", "support");

        using var response = await PostSearchAsync(client, withClientHeader: false);
        var result = await ReadAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Contains(MagicObjectHelper.ClientRequestHeader, result.ErrorMessage);
    }

    [Fact]
    public async Task BearerWrite_WithoutClientHeader_ShouldStillWork()
    {
        // 外部用戶端不靠 Cookie，沒有 CSRF 問題，不需要防偽標頭。
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = "support", Password = "support" });
        var token = (await ReadAsync<TokenResponseDto>(login)).Data!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await PostSearchAsync(client, withClientHeader: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Api_ShouldReturn401ApiResult_NotRedirectToLoginPage()
    {
        // 以前 Cookie 未登入一律 302 到登入頁；前端只會拿到一頁 HTML，分不出是登入失效。
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/api/v1/session");
        var result = await ReadAsync<SessionDto>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task Session_Admin_ShouldReturnActiveAdmin()
    {
        using var client = await CookieClientAsync("support", "support");

        var session = (await ReadAsync<SessionDto>(await client.GetAsync("/api/v1/session"))).Data!;

        Assert.Equal(SessionStatuses.Active, session.Status);
        Assert.True(session.IsAdmin);
        Assert.True(session.IsSupportAccount);
        Assert.Equal("support", session.Account);
        Assert.False(session.MustChangePassword);
    }

    [Fact]
    public async Task Session_LimitedUser_KeysAndMenu_ShouldMatchServerRules()
    {
        var (account, userId) = await CreateUserAsync(["會議紀錄"]);
        using var client = await CookieClientAsync(account, Password);

        var session = (await ReadAsync<SessionDto>(await client.GetAsync("/api/v1/session"))).Data!;
        var menu = (await ReadAsync<List<MenuItemDto>>(await client.GetAsync("/api/v1/session/menu"))).Data!;

        using (var scope = factory.Services.CreateScope())
        {
            var expected = await scope.ServiceProvider.GetRequiredService<IPermissionChecker>().GetEffectivePermissionKeysAsync(userId);
            Assert.Equal(expected.Order(StringComparer.Ordinal), session.PermissionKeys);
        }

        Assert.False(session.IsAdmin);
        var ids = Flatten(menu).Select(x => x.Id).ToHashSet();
        Assert.Contains(61, ids);       // 會議紀錄
        Assert.DoesNotContain(31, ids); // 使用者管理（沒有權限）
        Assert.DoesNotContain(34, ids); // 系統健康監控（只限管理者）
    }

    [Fact]
    public async Task Menu_Admin_ShouldIncludeAdminOnlyItems()
    {
        using var client = await CookieClientAsync("support", "support");

        var menu = (await ReadAsync<List<MenuItemDto>>(await client.GetAsync("/api/v1/session/menu"))).Data!;

        var ids = Flatten(menu).Select(x => x.Id).ToHashSet();
        Assert.Contains(31, ids);
        Assert.Contains(34, ids);
    }

    [Fact]
    public async Task Session_MustChangePassword_ShouldSaySo()
    {
        var (account, _) = await CreateUserAsync(["會議紀錄"], mustChangePassword: true);
        using var client = await CookieClientAsync(account, Password);

        var session = (await ReadAsync<SessionDto>(await client.GetAsync("/api/v1/session"))).Data!;

        Assert.True(session.MustChangePassword);
    }

    [Fact]
    public async Task Session_DisabledAfterLogin_ShouldBe403WithLogoutReason()
    {
        var (account, userId) = await CreateUserAsync(["會議紀錄"]);
        using var client = await CookieClientAsync(account, Password);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, false));
        }

        using var response = await client.GetAsync("/api/v1/session");
        var result = await ReadAsync<SessionDto>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(LogoutReasons.Disabled, result.Data!.Status);
    }

    /// <summary>走真的登入頁表單，回傳帶著登入 Cookie 的用戶端。</summary>
    private async Task<HttpClient> CookieClientAsync(string account, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, password);
        return client;
    }

    private Task<(string Account, int UserId)> CreateUserAsync(string[] permissions, bool mustChangePassword = false)
        => TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions, mustChangePassword);

    private static Task<HttpResponseMessage> PostSearchAsync(HttpClient client, bool withClientHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Category/search")
        {
            Content = JsonContent.Create(new { PageIndex = 1, PageSize = 10 }),
        };
        if (withClientHeader)
        {
            request.Headers.Add(MagicObjectHelper.ClientRequestHeader, "1");
        }

        return client.SendAsync(request);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");

    private static IEnumerable<MenuItemDto> Flatten(IEnumerable<MenuItemDto> items)
        => items.SelectMany(x => new[] { x }.Concat(Flatten(x.SubMenu)));
}

/// <summary>從登入身分取使用者 Id：Cookie 與 JWT 放的地方不同（1.1.0）。</summary>
public sealed class UserClaimsTests
{
    [Fact]
    public void Cookie_IdInSid_AccountInNameIdentifier_ShouldReadSid()
        => Assert.Equal(7, UserClaims.GetUserId(Principal(new Claim(ClaimTypes.Sid, "7"), new Claim(ClaimTypes.NameIdentifier, "alice"))));

    [Fact]
    public void Jwt_IdInNameIdentifier_ShouldReadIt()
        => Assert.Equal(9, UserClaims.GetUserId(Principal(new Claim(ClaimTypes.NameIdentifier, "9"))));

    [Fact]
    public void AccountOnly_ShouldBeNull()
        => Assert.Null(UserClaims.GetUserId(Principal(new Claim(ClaimTypes.NameIdentifier, "alice"))));

    [Fact]
    public void Unauthenticated_ShouldBeNull()
        => Assert.Null(UserClaims.GetUserId(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Sid, "7")]))));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));
}
