using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
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

        var tokenResponse = jwtTokenService.CreateTokenResponse(user);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public ActionResult<ApiResult<TokenResponseDto>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        try
        {
            var currentUser = jwtTokenService.ValidateRefreshToken(request.RefreshToken);
            var user = new MyUser
            {
                Id = currentUser.Id,
                Account = currentUser.Account,
                Name = currentUser.Name,
                Email = currentUser.Email,
                IsAdmin = currentUser.IsAdmin
            };

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
