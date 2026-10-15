using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Others;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>稽核的「目前操作者」（1.0.34）：Blazor 用 CurrentUserService，API 從 claims 取。</summary>
public sealed class AuditActorProviderTests
{
    [Fact]
    public void BlazorUser_ShouldWinOverHttpContext()
    {
        var current = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "wang" } };
        var provider = new AuditActorProvider(current, Accessor(Jwt(userId: 9, account: "api-user")));

        Assert.Equal((7, "wang"), provider.GetActor());
    }

    /// <summary>JWT：NameIdentifier＝編號、Name＝帳號（與 Cookie 相反）。</summary>
    [Fact]
    public void ApiRequest_ShouldReadJwtClaims()
    {
        var provider = new AuditActorProvider(new CurrentUserService(), Accessor(Jwt(userId: 9, account: "api-user")));

        Assert.Equal((9, "api-user"), provider.GetActor());
    }

    /// <summary>Cookie：Sid＝編號、NameIdentifier＝帳號、Name＝姓名（不取用）。</summary>
    [Fact]
    public void CookieRequest_ShouldReadSidAndAccount()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, "3"), new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim(ClaimTypes.Name, "Alice Chen")],
            "Cookie"));

        var provider = new AuditActorProvider(new CurrentUserService(), Accessor(principal));

        Assert.Equal((3, "alice"), provider.GetActor());
    }

    [Fact]
    public void NobodyLoggedIn_ShouldReturnNulls()
    {
        var provider = new AuditActorProvider(new CurrentUserService(), new HttpContextAccessor());

        Assert.Equal(((int?)null, (string?)null), provider.GetActor());
    }

    private static ClaimsPrincipal Jwt(int userId, string account)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, account)],
            "Bearer"));

    private static HttpContextAccessor Accessor(ClaimsPrincipal principal)
        => new() { HttpContext = new DefaultHttpContext { User = principal } };
}
