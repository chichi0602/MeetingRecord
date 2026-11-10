using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 自己的帳號（1.1.6，WASM 遷移）：目前只有變更密碼，規則與 Server 頁使用者選單的對話窗相同。
/// 只需登入、只動呼叫者自己；須改密碼的帳號也要能用（就是要它改密碼）。
/// </summary>
[Route("api/v1/account")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class AccountController(MyUserService myUserService, IConfiguration configuration) : ControllerBase
{
    public const int MinPasswordLength = 6;

    [HttpPost("change-password")]
    [AllowWhenMustChangePassword]
    public async Task<ActionResult<ApiResult>> ChangePassword([FromBody] ChangePasswordRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        var user = await myUserService.GetAsync(userId);
        var supportAccount = configuration["BootstrapSettings:SupportAccount"] ?? "support";
        if (string.Equals(user.Account, supportAccount, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResult.ForbiddenResult($"系統預設帳號 {supportAccount} 不允許變更密碼。"));
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return BadRequest(ApiResult.FailureResult("請輸入目前密碼。"));
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < MinPasswordLength)
        {
            return BadRequest(ApiResult.FailureResult($"新密碼至少需要 {MinPasswordLength} 個字元。"));
        }

        // 服務會驗目前密碼、清掉「須改密碼」標記、寫稽核。
        var result = await myUserService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
        return result.Success
            ? Ok(ApiResult.SuccessResult("密碼變更成功"))
            : BadRequest(ApiResult.FailureResult(result.Message ?? "變更密碼失敗，請稍後再試。"));
    }
}
