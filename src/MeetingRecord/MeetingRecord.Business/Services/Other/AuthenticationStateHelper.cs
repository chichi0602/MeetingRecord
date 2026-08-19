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

    public AuthenticationStateHelper(
        ILogger<AuthenticationStateHelper> logger,
        IMapper mapper,
        MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPermissionChecker permissionChecker)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.permissionChecker = permissionChecker;
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
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.Unauthenticated;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Authentication check failed because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        var myUser = await myUserService.GetAsync(id);
        if (myUser.Id == 0)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} was not found.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        if (!myUser.Status)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} is disabled.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
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
            navigationManager.NavigateTo("/Auths/Logout", true, true);
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
            navigationManager.NavigateTo("/Auths/Logout", true, true);
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

        var result = currentUserService.CurrentUser.RoleList.Contains(name);
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
