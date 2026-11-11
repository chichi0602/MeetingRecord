using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Others;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>AI 呼叫的觸發者（1.1.28）：Blazor 與背景工作用 CurrentUserService，API 從 claims 取；名稱優先序 姓名 → 帳號。</summary>
public sealed class AiCallerProviderTests
{
    [Fact]
    public void BlazorUser_ShouldWinOverHttpContext()
    {
        var current = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Name = "王小明", Account = "wang" } };
        var provider = new AiCallerProvider(current, Accessor(Jwt(userId: 9, account: "api-user")));

        Assert.Equal((7, "王小明"), provider.GetCaller());
    }

    /// <summary>Cookie（WASM 頁）：Sid＝編號、Name＝姓名。</summary>
    [Fact]
    public void CookieRequest_ShouldUseSidAndDisplayName()
    {
        var provider = new AiCallerProvider(new CurrentUserService(), Accessor(Cookie(3, "alice", "Alice Chen")));

        Assert.Equal((3, "Alice Chen"), provider.GetCaller());
    }

    [Fact]
    public void CookieRequest_WithoutDisplayName_ShouldFallBackToAccount()
    {
        var provider = new AiCallerProvider(new CurrentUserService(), Accessor(Cookie(3, "alice", name: null)));

        Assert.Equal((3, "alice"), provider.GetCaller());
    }

    /// <summary>JWT：NameIdentifier＝編號、Name＝帳號（沒有姓名）。</summary>
    [Fact]
    public void JwtRequest_ShouldUseAccount()
    {
        var provider = new AiCallerProvider(new CurrentUserService(), Accessor(Jwt(userId: 9, account: "api-user")));

        Assert.Equal((9, "api-user"), provider.GetCaller());
    }

    [Fact]
    public void NobodyLoggedIn_ShouldReturnNulls()
    {
        var provider = new AiCallerProvider(new CurrentUserService(), new HttpContextAccessor());

        Assert.Equal(((int?)null, (string?)null), provider.GetCaller());
    }

    /// <summary>直接 new 服務、沒有 provider 的單元測試照舊只看 CurrentUser。</summary>
    [Fact]
    public void Attribution_WithoutProvider_ShouldUseCurrentUser()
        => Assert.Equal((7, "wang"), AiUsageAttribution.Resolve(new CurrentUser { Id = 7, Account = "wang" }, caller: null));

    private static ClaimsPrincipal Cookie(int userId, string account, string? name)
    {
        var claims = new List<Claim> { new(ClaimTypes.Sid, userId.ToString()), new(ClaimTypes.NameIdentifier, account) };
        if (name is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookie"));
    }

    private static ClaimsPrincipal Jwt(int userId, string account)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, account)],
            "Bearer"));

    private static HttpContextAccessor Accessor(ClaimsPrincipal principal)
        => new() { HttpContext = new DefaultHttpContext { User = principal } };
}
