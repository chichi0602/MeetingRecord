using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Web.Auth;
using System.Security.Claims;

namespace MeetingRecord.Web.Components.Auths
{
    public partial class Login
    {
        private const int CaptchaLength = 4;

        string errorMessage = string.Empty;
        string captchaCode = string.Empty;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        [Inject]
        public ILogger<Login> Logger { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public IOptions<GoogleOAuthSettings> GoogleOptions { get; set; } = default!;

        string message = string.Empty;

        private bool ShowGoogleLogin => GoogleOptions.Value.IsConfigured;

        private string GoogleLoginUrl =>
            string.IsNullOrWhiteSpace(ReturnUrl)
                ? "/Auths/Google/Login"
                : $"/Auths/Google/Login?returnUrl={Uri.EscapeDataString(ReturnUrl)}";

        protected override Task OnInitializedAsync()
        {
            Input ??= new();

            if (string.IsNullOrWhiteSpace(Input.CaptchaCode))
            {
                RefreshCaptcha();
            }
            else
            {
                captchaCode = Input.CaptchaCode;
            }

            Logger.LogDebug("Login component initialized. ReturnUrl={ReturnUrl}", ReturnUrl);
            return Task.CompletedTask;
        }

        public async Task LoginUser()
        {
            message = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(Input.Account))
            {
                message = "請輸入帳號";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because account is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.Password))
            {
                message = "請輸入密碼";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because password is empty. Account={Account}", Input.Account);
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.CaptchaInput))
            {
                message = "請輸入驗證碼";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because captcha is empty. Account={Account}", Input.Account);
                return;
            }

            if (!string.Equals(Input.CaptchaInput.Trim(), Input.CaptchaCode, StringComparison.Ordinal))
            {
                message = "驗證碼錯誤";
                errorMessage = "alert-danger";
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
                Logger.LogWarning("Login submission rejected because captcha is invalid. Account={Account}", Input.Account);
                return;
            }

            (string result, MyUser? myUser) = await MyUserServiceLogin.LoginAsync(Input.Account, Input.Password);
            if (!string.IsNullOrEmpty(result) || myUser is null)
            {
                Logger.LogWarning("Login failed for Account={Account}. Reason={Reason}", Input.Account, result);
                message = string.IsNullOrEmpty(result) ? "登入失敗，請重新確認帳號與密碼。" : result;
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
            }
            else
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Role, "User"),
                    new(ClaimTypes.Name, myUser.Name),
                    new(ClaimTypes.NameIdentifier, myUser.Account),
                    new(ClaimTypes.Sid, myUser.Id.ToString()),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                string returnUrl = string.IsNullOrEmpty(ReturnUrl) ? "/App" : ReturnUrl;
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = Input.RememberMe,
                    RedirectUri = returnUrl,
                };

                try
                {
                    await HttpContext.SignInAsync(
                        "CookieAuthenticationScheme",
                        new ClaimsPrincipal(claimsIdentity),
                        authProperties);

                    Logger.LogInformation(
                        "Login succeeded for Account={Account}, UserId={UserId}, RedirectUri={RedirectUri}.",
                        Input.Account,
                        myUser.Id,
                        returnUrl);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    RefreshCaptcha();
                    Input.CaptchaInput = string.Empty;
                    Logger.LogError(ex, "Sign-in failed for Account={Account}.", Input.Account);
                }
            }

            errorMessage = string.IsNullOrEmpty(message) ? string.Empty : "alert-danger";
        }

        private sealed class InputModel
        {
            public string Account { get; set; } = string.Empty;

            public string Password { get; set; } = string.Empty;

            public bool RememberMe { get; set; }

            public string CaptchaInput { get; set; } = string.Empty;

            public string CaptchaCode { get; set; } = string.Empty;
        }

        private void RefreshCaptcha()
        {
            captchaCode = GenerateCaptcha();
            Input.CaptchaCode = captchaCode;
        }

        private static string GenerateCaptcha()
        {
            return Random.Shared.Next(0, (int)Math.Pow(10, CaptchaLength)).ToString($"D{CaptchaLength}");
        }
    }
}
