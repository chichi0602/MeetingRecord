using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using System.Security.Claims;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// Google OAuth2 第三方登入的瀏覽器導向端點（Challenge / Callback）。
/// 僅負責網頁 Cookie 登入；API 仍使用既有的帳密換 JWT 流程。
/// </summary>
[Route("Auths/Google")]
public class ExternalAuthController : Controller
{
    private readonly ExternalLoginService externalLoginService;
    private readonly GoogleOAuthSettings googleOAuthSettings;
    private readonly ILogger<ExternalAuthController> logger;

    public ExternalAuthController(
        ExternalLoginService externalLoginService,
        IOptions<GoogleOAuthSettings> googleOAuthSettings,
        ILogger<ExternalAuthController> logger)
    {
        this.externalLoginService = externalLoginService;
        this.googleOAuthSettings = googleOAuthSettings.Value;
        this.logger = logger;
    }

    /// <summary>
    /// 觸發 Google OAuth2 驗證。
    /// </summary>
    [HttpGet("Login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!googleOAuthSettings.IsConfigured)
        {
            logger.LogWarning("Google login requested but Google OAuth is not configured.");
            return Redirect("/Auths/Login");
        }

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var callbackUrl = Url.Action(nameof(Callback), "ExternalAuth", new { returnUrl = safeReturnUrl })
            ?? "/Auths/Google/Callback";

        var properties = new AuthenticationProperties
        {
            RedirectUri = callbackUrl,
        };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Google 驗證完成後的回呼：查找/建立帳號，依狀態導向待審核頁或完成 Cookie 登入。
    /// </summary>
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        var result = await HttpContext.AuthenticateAsync(MagicObjectHelper.ExternalCookieScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            logger.LogWarning("Google callback failed because external authentication did not succeed.");
            return Redirect("/Auths/Login");
        }

        var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        var name = result.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Google callback rejected because subject or email claim is missing.");
            await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);
            return Redirect("/Auths/Login");
        }

        var user = await externalLoginService.FindOrCreateAsync(
            GoogleDefaults.AuthenticationScheme,
            subject,
            email,
            name,
            googleOAuthSettings.DefaultRoleName);

        // 清除暫存的外部登入身分
        await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);

        if (!user.Status)
        {
            logger.LogInformation(
                "Google login user is disabled and awaiting approval. UserId={UserId}, Email={Email}.",
                user.Id, email);
            return Redirect("/Auths/Pending");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, "User"),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.NameIdentifier, user.Account),
            new(ClaimTypes.Sid, user.Id.ToString()),
        };

        var claimsIdentity = new ClaimsIdentity(claims, MagicObjectHelper.CookieScheme);
        await HttpContext.SignInAsync(
            MagicObjectHelper.CookieScheme,
            new ClaimsPrincipal(claimsIdentity));

        logger.LogInformation(
            "Google login succeeded. UserId={UserId}, Account={Account}.",
            user.Id, user.Account);

        return Redirect(GetSafeReturnUrl(returnUrl));
    }

    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return "/App";
    }
}
