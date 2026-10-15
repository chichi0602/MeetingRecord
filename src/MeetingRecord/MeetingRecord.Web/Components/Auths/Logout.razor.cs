using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Components.Auths
{
    public partial class Logout
    {
        private readonly ILogger<Logout> logger;
        string errorMessage = string.Empty;

        public Logout(ILogger<Logout> logger)
        {
            this.logger = logger;
        }

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IAuditLogService AuditLogService { get; set; } = default!;

        /// <summary>
        /// 被系統強制登出的原因（1.0.33，<see cref="LogoutReasons"/>）。由 <c>AuthenticationStateHelper.Check</c> 帶過來；
        /// 使用者自己按登出時沒有。
        /// </summary>
        [SupplyParameterFromQuery(Name = "reason")]
        public string? Reason { get; set; }

        protected override async Task OnInitializedAsync()
        {
            // ⚠️ 一定要在 SignOutAsync 之前取身分：登出之後 HttpContext.User 就不是這個人了。
            var (account, userId) = RequestActorResolver.Resolve(HttpContext.User);

            try
            {
                await HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
                await Task.Delay(200);
                logger.LogInformation("User logout completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Logout flow failed unexpectedly.");
                errorMessage = ex.Message;
            }

            if (BuildAudit(Reason, userId, account) is { } audit)
            {
                await AuditLogService.WriteAsync(
                    audit.Action,
                    success: true,
                    actorUserId: userId,
                    actorAccount: account,
                    detail: audit.Detail);
            }

            NavigationManager.NavigateTo("/Auths/Login", forceLoad: true);
        }

        /// <summary>
        /// 登出要寫哪一種稽核（1.0.33）。抽成純函式以便單元測試：
        /// <list type="bullet">
        /// <item>原因在 <see cref="LogoutReasons.All"/> 裡：被系統登出（沒有身分也記，例如 Sid 無效）。</item>
        /// <item>沒有原因或原因不認得、有登入身分：使用者自己登出。</item>
        /// <item>沒有身分也沒有可認的原因（未登入的人開了登出頁）：不寫。</item>
        /// </list>
        /// 使用者手動打 <c>?reason=disabled</c> 只會把自己的登出記成被系統登出，沒有危害。
        /// </summary>
        internal static (string Action, string Detail)? BuildAudit(string? reason, int? userId, string? account)
        {
            if (reason is not null && LogoutReasons.All.Contains(reason))
            {
                return (AuditActions.LogoutForced, $"reason={reason}");
            }

            if (userId is null && string.IsNullOrEmpty(account))
            {
                return null;
            }

            return (AuditActions.LogoutManual, "reason=user");
        }
    }
}
