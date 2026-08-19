using MeetingRecord.Models.Others;

namespace MeetingRecord.Business.Services.Other;

public class CurrentUserService
{
    public CurrentUser CurrentUser { get; set; } = new();
}
