namespace MeetingRecord.Dtos.Auths;

public class CurrentUserDto
{
    public int Id { get; set; }

    public string Account { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool IsAdmin { get; set; }
}
