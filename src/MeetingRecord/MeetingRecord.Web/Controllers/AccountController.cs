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
/// 自己的帳號（1.1.6）。只需登入、只動呼叫者自己。
/// <list type="bullet">
/// <item><c>change-password</c>：使用者選單的對話窗（至少 6 字）。</item>
/// <item><c>change-own-password</c>、<c>password-status</c>、<c>api-password</c>（1.1.13）：變更密碼頁與設定 API 密碼頁，規則照原頁
/// （<see cref="MyUserService.ChangeOwnPasswordAsync"/>、<see cref="MyUserService.SetOwnApiPasswordAsync"/>）。</item>
/// </list>
/// 須改密碼的帳號也要能改密碼（就是要它改），所以改密碼的端點標 <see cref="AllowWhenMustChangePasswordAttribute"/>。
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

    /// <summary>變更密碼頁（第一次登入要改密碼也用這支）：要確認新密碼，稽核記 <c>via=FirstLogin</c>，規則與訊息照原頁。</summary>
    [HttpPost("change-own-password")]
    [AllowWhenMustChangePassword]
    public async Task<ActionResult<ApiResult>> ChangeOwnPassword([FromBody] OwnPasswordRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        var result = await myUserService.ChangeOwnPasswordAsync(userId, request.CurrentPassword ?? string.Empty, request.NewPassword, request.ConfirmPassword);
        return result.Success
            ? Ok(ApiResult.SuccessResult("密碼已變更。"))
            : BadRequest(ApiResult.FailureResult(result.Message));
    }

    /// <summary>自己是否已有本地密碼（設定 API 密碼頁決定要不要顯示「目前密碼」）。</summary>
    [HttpGet("password-status")]
    public async Task<ActionResult<ApiResult<PasswordStatusDto>>> GetPasswordStatus()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<PasswordStatusDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        return Ok(ApiResult<PasswordStatusDto>.SuccessResult(new PasswordStatusDto { HasLocalPassword = await myUserService.HasLocalPasswordAsync(userId) }));
    }

    /// <summary>設定 API 密碼：已有本地密碼要驗目前密碼；還沒有的（例如 Google 帳號）免驗。</summary>
    [HttpPost("api-password")]
    public async Task<ActionResult<ApiResult>> SetApiPassword([FromBody] OwnPasswordRequestDto request)
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult.UnauthorizedResult("無法識別使用者身分。"));
        }

        var result = await myUserService.SetOwnApiPasswordAsync(userId, request.CurrentPassword, request.NewPassword, request.ConfirmPassword);
        return result.Success
            ? Ok(ApiResult.SuccessResult("API 密碼已設定。"))
            : BadRequest(ApiResult.FailureResult(result.Message));
    }
}
