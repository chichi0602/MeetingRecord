namespace MeetingRecord.Business.Services.Other;

public enum AuthenticationCheckResult
{
    Succeeded,
    Unauthenticated,
    InvalidUser,
    RequiresPasswordChange
}
