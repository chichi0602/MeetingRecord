using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Dtos.Auths;

public class LoginRequestDto
{
    [Required(ErrorMessage = "帳號不可為空白")]
    public string Account { get; set; } = string.Empty;

    [Required(ErrorMessage = "密碼不可為空白")]
    public string Password { get; set; } = string.Empty;
}
