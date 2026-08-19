using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Web.Auth;

public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 7;

    [Range(0, 60)]
    public int ClockSkewMinutes { get; set; } = 2;
}
