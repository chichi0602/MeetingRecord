using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Tests;

/// <summary>
/// Web API 限流（1.0.63）：依使用者分開計算。1.0.62 以前全站共用一個桶、每分鐘 120 次，
/// 一個人批次呼叫會讓所有人一起收到 429。
/// </summary>
public sealed class RateLimitPartitionTests
{
    [Fact]
    public void CookieUser_ShouldBePartitionedBySid_AsWeb()
        => Assert.Equal("web:7", MeetingRecord.Web.Extensions.ServiceCollectionExtensions.ResolveRateLimitPartitionKey(Context(new Claim(ClaimTypes.Sid, "7"), new Claim(ClaimTypes.NameIdentifier, "alice"))));

    [Fact]
    public void JwtUser_ShouldBePartitionedByNameIdentifier()
    {
        // 1.1.19：帶 Bearer 的是外部程式，與同一個人在網頁上的呼叫分開計算。
        var context = Context(new Claim(ClaimTypes.NameIdentifier, "9"));
        context.Request.Headers.Authorization = "Bearer abc";
        Assert.Equal("user:9", MeetingRecord.Web.Extensions.ServiceCollectionExtensions.ResolveRateLimitPartitionKey(context));
    }

    [Fact]
    public void Anonymous_ShouldBePartitionedByIp()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");

        Assert.Equal("ip:203.0.113.5", MeetingRecord.Web.Extensions.ServiceCollectionExtensions.ResolveRateLimitPartitionKey(context));
    }

    [Fact]
    public void Anonymous_WithoutIp_ShouldShareOneBucket()
        => Assert.Equal("ip:unknown", MeetingRecord.Web.Extensions.ServiceCollectionExtensions.ResolveRateLimitPartitionKey(new DefaultHttpContext()));

    [Fact]
    public void Settings_ShouldDefaultToPerUser120_PerAnonymousIp30_PerMinute()
    {
        var settings = new RateLimitSettings();

        Assert.Equal(120, settings.PermitLimitPerUser);
        Assert.Equal(600, settings.PermitLimitPerWebUser);
        Assert.Equal(30, settings.PermitLimitPerAnonymousIp);
        Assert.Equal(60, settings.WindowSeconds);
    }

    private static DefaultHttpContext Context(params Claim[] claims)
        => new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
}

/// <summary>限流調低（每人／每 IP 每分鐘 3 次）的測試主機。</summary>
public sealed class RateLimitTestApplicationFactory : ApiTestApplicationFactory
{
    protected override Dictionary<string, string?> CreateSettings()
    {
        var settings = base.CreateSettings();
        settings["RateLimitSettings:PermitLimitPerUser"] = "3";
        settings["RateLimitSettings:PermitLimitPerWebUser"] = "5";
        settings["RateLimitSettings:PermitLimitPerAnonymousIp"] = "3";
        return settings;
    }
}

[Collection(nameof(ApiHostCollection))]
public sealed class RateLimitIntegrationTests(RateLimitTestApplicationFactory factory) : IClassFixture<RateLimitTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task EachUser_ShouldHaveOwnQuota_AndRejectionShouldBeApiResult()
    {
        // 依序：兩次登入（未登入、依 IP 計 2 次）→ 甲用完 3 次 → 甲第 4 次 429 → 乙照常 → 未登入第 4 次 429。
        // 整則寫在一起：同一台主機的額度以分鐘計，拆成多則會互相影響。
        const string password = "rate-limit-pass";
        var other = $"rate-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            db.MyUser.Add(new MyUser { Account = other, Name = other, Status = true, IsAdmin = true, Password = SecurePasswordHasher.HashPassword(password) });
            await db.SaveChangesAsync();
        }

        using var userA = factory.CreateClient();
        using var userB = factory.CreateClient();
        await LoginAsync(userA, "support", "support");
        await LoginAsync(userB, other, password);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await userA.GetAsync("/api/Auth/me");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await userA.GetAsync("/api/Auth/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = JsonSerializer.Deserialize<ApiResult>(await rejected.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(body);
        Assert.False(body!.Success);
        Assert.Equal(429, body.StatusCode);
        Assert.True(rejected.Headers.RetryAfter is not null, "429 要帶 Retry-After");

        // 甲被擋不影響乙。
        using var otherUser = await userB.GetAsync("/api/Auth/me");
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);

        // 未登入依 IP：前面兩次登入已用掉 2 次，再 1 次可以，第 4 次被擋。
        using var anonymous = factory.CreateClient();
        using (var third = await anonymous.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = "nobody", Password = "x" }))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, third.StatusCode);
        }

        using var fourth = await anonymous.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = "nobody", Password = "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    [Fact]
    public async Task WebPageCalls_ShouldUseTheHigherWebQuota()
    {
        // 1.1.19：網頁本身靠登入 Cookie 呼叫 API，額度另計（這台主機設 5 次），不受外部程式的 3 次限制。
        const string password = "rate-limit-web-pass";
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, password, []);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, password);

        for (var i = 0; i < 5; i++)
        {
            using var ok = await client.GetAsync("/api/v1/session");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var rejected = await client.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, string account, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var result = JsonSerializer.Deserialize<ApiResult<TokenResponseDto>>(await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.True(result?.Success == true, $"{account} 登入失敗：{(int)response.StatusCode}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result!.Data!.AccessToken);
    }
}
