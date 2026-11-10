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

    /// <summary>變更密碼頁（1.1.13）：要確認新密碼；須改密碼的帳號也能用。</summary>
    public Task<ApiResult<object>> ChangeOwnPasswordAsync(string currentPassword, string newPassword, string confirmPassword)
        => api.PostAsync(
            "api/v1/account/change-own-password",
            new OwnPasswordRequestDto { CurrentPassword = currentPassword, NewPassword = newPassword, ConfirmPassword = confirmPassword },
            ClientJsonContext.Default.OwnPasswordRequestDto,
            ClientJsonContext.Default.EmptyResult);

    /// <summary>自己是否已有本地密碼（1.1.13）。</summary>
    public Task<ApiResult<PasswordStatusDto>> GetPasswordStatusAsync()
        => api.GetAsync("api/v1/account/password-status", ClientJsonContext.Default.PasswordStatusResult);

    /// <summary>設定 API 密碼（1.1.13）：還沒有本地密碼的帳號免填目前密碼。</summary>
    public Task<ApiResult<object>> SetApiPasswordAsync(string? currentPassword, string newPassword, string confirmPassword)
        => api.PostAsync(
            "api/v1/account/api-password",
            new OwnPasswordRequestDto { CurrentPassword = currentPassword, NewPassword = newPassword, ConfirmPassword = confirmPassword },
            ClientJsonContext.Default.OwnPasswordRequestDto,
            ClientJsonContext.Default.EmptyResult);
}
