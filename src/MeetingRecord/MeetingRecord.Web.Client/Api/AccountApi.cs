using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>自己的帳號（1.1.6）：變更密碼。</summary>
public sealed class AccountApi(ApiClient api)
{
    public Task<ApiResult<object>> ChangePasswordAsync(string currentPassword, string newPassword)
        => api.PostAsync(
            "api/v1/account/change-password",
            new ChangePasswordRequestDto { CurrentPassword = currentPassword, NewPassword = newPassword },
            ClientJsonContext.Default.ChangePasswordRequestDto,
            ClientJsonContext.Default.EmptyResult);
}
