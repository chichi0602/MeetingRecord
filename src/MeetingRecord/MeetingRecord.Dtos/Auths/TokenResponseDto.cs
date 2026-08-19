namespace MeetingRecord.Dtos.Auths;

public class TokenResponseDto
{
    public string TokenType { get; set; } = "Bearer";

    public string AccessToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAt { get; set; }

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime RefreshTokenExpiresAt { get; set; }

    public CurrentUserDto User { get; set; } = new();
}
