using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Auths;

namespace MeetingRecord.Web.Auth;

public class JwtTokenService : IJwtTokenService
{
    private const string RefreshTokenType = "refresh";
    private const string AccessTokenType = "access";
    private readonly JwtSettings settings;

    public JwtTokenService(IOptions<JwtSettings> options)
    {
        settings = options.Value;
    }

    public TokenResponseDto CreateTokenResponse(MyUser user)
    {
        var currentUser = ToCurrentUserDto(user);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenDays);

        return new TokenResponseDto
        {
            AccessToken = CreateToken(currentUser, AccessTokenType, accessExpiresAt),
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = CreateToken(currentUser, RefreshTokenType, refreshExpiresAt),
            RefreshTokenExpiresAt = refreshExpiresAt,
            User = currentUser
        };
    }

    public CurrentUserDto ValidateRefreshToken(string refreshToken)
    {
        var principal = new JwtSecurityTokenHandler().ValidateToken(
            refreshToken,
            CreateValidationParameters(validateLifetime: true),
            out _);

        var tokenType = principal.FindFirstValue("token_type");
        if (!string.Equals(tokenType, RefreshTokenType, StringComparison.Ordinal))
        {
            throw new SecurityTokenException("Token 類型不是 refresh token。");
        }

        return new CurrentUserDto
        {
            Id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0"),
            Account = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            Name = principal.FindFirstValue("display_name") ?? string.Empty,
            Email = principal.FindFirstValue(ClaimTypes.Email),
            IsAdmin = bool.TryParse(principal.FindFirstValue("is_admin"), out var isAdmin) && isAdmin
        };
    }

    public TokenValidationParameters CreateValidationParameters(bool validateLifetime)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateSecurityKey(),
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromMinutes(settings.ClockSkewMinutes)
        };
    }

    private string CreateToken(CurrentUserDto user, string tokenType, DateTime expiresAt)
    {
        var credentials = new SigningCredentials(CreateSecurityKey(), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Account),
            new("display_name", user.Name),
            new("is_admin", user.IsAdmin.ToString()),
            new("token_type", tokenType)
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private SymmetricSecurityKey CreateSecurityKey()
    {
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
    }

    private static CurrentUserDto ToCurrentUserDto(MyUser user)
    {
        return new CurrentUserDto
        {
            Id = user.Id,
            Account = user.Account,
            Name = user.Name,
            Email = user.Email,
            IsAdmin = user.IsAdmin
        };
    }
}
