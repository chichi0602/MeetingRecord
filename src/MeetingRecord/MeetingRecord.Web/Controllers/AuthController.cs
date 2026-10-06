using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
public class AuthController : ControllerBase
{
    private readonly MyUserServiceLogin userServiceLogin;
    private readonly IJwtTokenService jwtTokenService;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        MyUserServiceLogin userServiceLogin,
        IJwtTokenService jwtTokenService,
        ILogger<AuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.jwtTokenService = jwtTokenService;
        this.logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Login([FromBody] LoginRequestDto request)
    {
        var (message, user) = await userServiceLogin.LoginAsync(request.Account, request.Password);
        if (user is null)
        {
            logger.LogWarning("API login failed. Account={Account}", request.Account);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(message));
        }

        // 強制改密碼在 API 也要生效（0.4.115）：以前只有網頁會導到改密碼頁，
        // 管理者剛發的初始密碼可以直接拿來用 API，永遠不必改。
        if (user.MustChangePassword || request.Password == MagicObjectHelper.NeedChangePassword)
        {
            logger.LogWarning("API login rejected because password change is required. Account={Account}, UserId={UserId}", user.Account, user.Id);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("請先登入網頁變更密碼後，再使用 API。"));
        }

        var tokenResponse = jwtTokenService.CreateTokenResponse(user);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        try
        {
            var currentUser = jwtTokenService.ValidateRefreshToken(request.RefreshToken);

            // 以資料庫為準重簽（0.4.115）：停用、鎖定、刪除或待改密碼的帳號不換發，
            // 管理者身分、名稱也用資料庫現值，不沿用舊權杖裡的內容。
            var user = await userServiceLogin.GetUserForTokenRefreshAsync(currentUser.Id);
            if (user is null)
            {
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("帳號狀態已變更，請重新登入。"));
            }

            var tokenResponse = jwtTokenService.CreateTokenResponse(user);
            return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "Token 更新成功"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refresh token validation failed.");
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
        }
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public ActionResult<ApiResult<CurrentUserDto>> Me()
    {
        var user = new CurrentUserDto
        {
            Id = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0,
            Account = User.Identity?.Name ?? string.Empty,
            Name = User.FindFirst("display_name")?.Value ?? string.Empty,
            Email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            IsAdmin = bool.TryParse(User.FindFirst("is_admin")?.Value, out var isAdmin) && isAdmin
        };

        return Ok(ApiResult<CurrentUserDto>.SuccessResult(user, "取得目前使用者成功"));
    }
}
