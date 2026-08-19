using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

public class JwtTokenServiceTests
{
    [Fact]
    public void CreateTokenResponse_ShouldCreateAccessAndRefreshTokens()
    {
        var service = CreateService();
        var user = CreateUser();

        var result = service.CreateTokenResponse(user);

        Assert.Equal("Bearer", result.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.Equal(user.Account, result.User.Account);
        Assert.True(result.AccessTokenExpiresAt > DateTime.UtcNow);
        Assert.True(result.RefreshTokenExpiresAt > result.AccessTokenExpiresAt);
    }

    [Fact]
    public void ValidateRefreshToken_ShouldReturnCurrentUser()
    {
        var service = CreateService();
        var tokenResponse = service.CreateTokenResponse(CreateUser());

        var user = service.ValidateRefreshToken(tokenResponse.RefreshToken);

        Assert.Equal(7, user.Id);
        Assert.Equal("api-user", user.Account);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public void ValidateRefreshToken_ShouldRejectAccessToken()
    {
        var service = CreateService();
        var tokenResponse = service.CreateTokenResponse(CreateUser());

        Assert.ThrowsAny<Exception>(() => service.ValidateRefreshToken(tokenResponse.AccessToken));
    }

    private static JwtTokenService CreateService()
    {
        var settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "TestSigningKey-AtLeast32Characters-12345",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
            ClockSkewMinutes = 0
        };

        return new JwtTokenService(Options.Create(settings));
    }

    private static MyUser CreateUser()
    {
        return new MyUser
        {
            Id = 7,
            Account = "api-user",
            Name = "API User",
            Email = "api-user@example.com",
            IsAdmin = true
        };
    }
}
