using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Admins;
using MeetingRecord.Models.Others;
using MeetingRecord.Share.Helpers;
using System.Security.Claims;
using System.Text.Json;

namespace MeetingRecord.Business.Services.Other;

public class AuthenticationStateHelper
{
    private readonly ILogger<AuthenticationStateHelper> logger;
    private readonly IMapper mapper;
    private readonly MyUserService myUserService;
    private readonly CurrentUserService currentUserService;
    private readonly RolePermissionService rolePermissionService;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPermissionChecker permissionChecker;
    private readonly IAuditLogService auditLogService;

    public AuthenticationStateHelper(
        ILogger<AuthenticationStateHelper> logger,
        IMapper mapper,
        MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPermissionChecker permissionChecker,
        IAuditLogService auditLogService)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.permissionChecker = permissionChecker;
        this.auditLogService = auditLogService;
    }

    public async Task<AuthenticationCheckResult> Check(AuthenticationStateProvider authStateProvider, NavigationManager navigationManager)
    {
        logger.LogDebug("Checking authentication state for current request.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Authentication check failed because the current principal is not authenticated.");
            await Task.Delay(200);
            // ⚠️ 這裡導向登入頁而**不是**登出頁（0.4.119 起），其餘分支則維持導向登出。
            // 原因：/Auths/Logout 會無條件呼叫 SignOutAsync 刪掉 Cookie。而「未認證」這個
            // 分支同時涵蓋「Cookie 一時讀不出來」（伺服器重啟、Data Protection 金鑰換掉、
            // 換連接埠或網域）—— 那種情況下把 Cookie 毀掉，會讓使用者原本還有效的
            // 「記住我」永久消失。真正失效的 Cookie 由 Cookie handler 自己清理，不需要我們動手。
            // ⚠️ 不會產生重導迴圈：登入頁用的是 NoFooterLayout，而那個版面不呼叫本方法。
            navigationManager.NavigateTo("/Auths/Login", true, true);
            return AuthenticationCheckResult.Unauthenticated;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Authentication check failed because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            await Task.Delay(200);
            navigationManager.NavigateTo(LogoutReasons.LogoutUrl(LogoutReasons.InvalidClaim), true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        var myUser = await myUserService.GetAsync(id);
        if (myUser.Id == 0)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} was not found.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo(LogoutReasons.LogoutUrl(LogoutReasons.Deleted), true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        if (!myUser.Status)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} is disabled.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo(LogoutReasons.LogoutUrl(LogoutReasons.Disabled), true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        logger.LogDebug("Resolved authenticated user information for UserId={UserId}.", id);

        bool needChangePassword = await myUserService.NeedChangePasswordAsync(myUser);
        if (needChangePassword && !IsChangePasswordPage(navigationManager))
        {
            logger.LogWarning("User {UserId} is required to change password before continuing.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/ChangePassword", true);
            return AuthenticationCheckResult.RequiresPasswordChange;
        }

        CurrentUser currentUser = mapper.Map<CurrentUser>(myUser);
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();

        if (myUser.RoleView == null)
        {
            logger.LogWarning("User {UserId} does not have a role view assigned. Redirecting to logout.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo(LogoutReasons.LogoutUrl(LogoutReasons.NoRole), true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        try
        {
            List<string> permissions = JsonSerializer.Deserialize<List<string>>(myUser.RoleView.TabViewJson) ?? [];
            rolePermissionService.SetPermissionInput(rolePermission, permissions);
            currentUser.RoleJson = myUser.RoleView.TabViewJson;
            currentUser.TeamList = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(myUser.Id)).ToList();
            currentUser.IsAuthenticated = true;
            currentUserService.CurrentUser.CopyFrom(currentUser);

            // UI 權限判定改以 RBAC 表為單一權威來源（含多角色聯集），與 API 端 IPermissionChecker 對齊；
            // 覆寫 CopyFrom 由 TabViewJson 反序列化得到的 RoleList。TabViewJson 僅保留供角色編輯畫面回填。
            currentUserService.CurrentUser.RoleList =
                (await permissionChecker.GetEffectivePermissionKeysAsync(myUser.Id)).ToList();

            logger.LogInformation(
                "Authentication state initialized for UserId={UserId}, Account={Account}, IsAdmin={IsAdmin}.",
                myUser.Id,
                myUser.Account,
                myUser.IsAdmin);

            return AuthenticationCheckResult.Succeeded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize permission data for UserId={UserId}.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo(LogoutReasons.LogoutUrl(LogoutReasons.PermissionError), true, true);
            return AuthenticationCheckResult.InvalidUser;
        }
    }

    private static bool IsChangePasswordPage(NavigationManager navigationManager)
    {
        var currentPath = new Uri(navigationManager.Uri).AbsolutePath.Trim('/');
        return string.Equals(currentPath, "ChangePassword", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MyUserAdapterModel?> GetUserInformation(AuthenticationStateProvider authStateProvider)
    {
        logger.LogDebug("Loading current user information from authentication state.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Cannot load current user information because the principal is not authenticated.");
            return null;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Cannot load current user information because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            return null;
        }

        return await myUserService.GetAsync(id);
    }

    /// <summary>
    /// 頁面擋下目前使用者時寫一筆 <see cref="AuditActions.PermissionDenied"/>（1.0.33）。
    /// 各頁在顯示「你沒有權限存取此頁面」的分支呼叫。
    /// ⚠️ 刻意不放進 <see cref="CheckAccessPage"/>／<see cref="CheckIsAdmin"/>：側邊欄過濾每個選單項目都會呼叫它們，
    /// 放進去每開一頁就洗出一堆拒絕紀錄（<c>PageAccessDeniedAuditTests</c> 守門每個拒絕分支都有呼叫）。
    /// </summary>
    /// <param name="route">被擋下的頁面路由，例如 <c>/audit-logs</c>。</param>
    public Task RecordPageAccessDeniedAsync(string route)
    {
        var currentUser = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            AuditActions.PermissionDenied,
            success: false,
            actorUserId: currentUser.Id > 0 ? currentUser.Id : null,
            actorAccount: currentUser.Id > 0 ? currentUser.Account : null,
            targetType: "Page",
            targetId: route,
            detail: $"via={AuditVia.Blazor}");
    }

    public bool CheckIsAdmin()
    {
        var isAdmin = currentUserService.CurrentUser.IsAdmin;
        logger.LogDebug("Checked admin permission for UserId={UserId}. IsAdmin={IsAdmin}", currentUserService.CurrentUser.Id, isAdmin);
        return isAdmin;
    }

    public bool CheckAccessPage(string name)
    {
        // 管理員一律通過：GetEffectivePermissionKeysAsync 不含 admin 隱含全通過（回空集合），
        // 故此處需比照 CheckAccessAction 短路，否則管理員選單/頁面會全被隱藏。
        if (currentUserService.CurrentUser.IsAdmin)
        {
            return true;
        }

        // 有整頁鍵（＝全部動作）或有「頁面:view」就能進頁面（0.4.109）。0.4.108 以前只認整頁鍵，
        // 只勾部分動作的角色（例如檢視者只有 專案項目:view）連頁面與選單都看不到。
        var roleList = currentUserService.CurrentUser.RoleList;
        var result = roleList.Contains(name) || roleList.Contains(PermissionKey.For(name, PermissionActions.View));
        logger.LogDebug(
            "Checked page access for UserId={UserId}, Page={PageName}, Allowed={Allowed}.",
            currentUserService.CurrentUser.Id,
            name,
            result);
        return result;
    }

    /// <summary>
    /// 檢查目前使用者是否具備某頁面的特定動作權限（view/create/edit/delete/export）。
    /// 管理員一律通過；擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。
    /// 供 Razor 檢視依動作顯示/停用按鈕使用。
    /// </summary>
    public bool CheckAccessAction(string page, string action)
    {
        var currentUser = currentUserService.CurrentUser;
        if (currentUser.IsAdmin)
        {
            return true;
        }

        var keys = currentUser.RoleList;
        return keys.Contains(PermissionKey.For(page, action)) || keys.Contains(page);
    }
}
