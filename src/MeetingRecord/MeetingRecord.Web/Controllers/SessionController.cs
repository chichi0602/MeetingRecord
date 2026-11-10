using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Components.Layout;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 目前登入者的狀態與選單（1.1.0，WASM 遷移 M1）。WASM 前端開頁時先問這裡，再決定導向（登出、改密碼）與畫選單。
///
/// <para>
/// 判斷規則與 Blazor Server 頁的 <see cref="AuthenticationStateHelper.Check"/> 同一份（<see cref="UserSessionService"/>）；
/// 選單過濾與網頁側邊欄同一份（<see cref="SidebarMenuService"/>）。只需登入、不看權限鍵——回的都是呼叫者自己的資料。
/// </para>
/// </summary>
[Route("api/v1/session")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
[AllowWhenMustChangePassword] // 須改密碼的人也要能問狀態，前端才知道該導去改密碼頁（1.1.4）。
public sealed class SessionController : ControllerBase
{
    private readonly UserSessionService userSessionService;
    private readonly IPermissionChecker permissionChecker;
    private readonly SidebarMenuService sidebarMenuService;
    private readonly IConfiguration configuration;

    public SessionController(
        UserSessionService userSessionService,
        IPermissionChecker permissionChecker,
        SidebarMenuService sidebarMenuService,
        IConfiguration configuration)
    {
        this.userSessionService = userSessionService;
        this.permissionChecker = permissionChecker;
        this.sidebarMenuService = sidebarMenuService;
        this.configuration = configuration;
    }

    /// <summary>目前登入者；刪除、停用、沒有角色時回 403，<c>Data.Status</c> 是登出原因。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResult<SessionDto>>> Get()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<SessionDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        var evaluation = await userSessionService.EvaluateAsync(userId);
        if (evaluation.LogoutReason is { } reason)
        {
            return Blocked(reason);
        }

        var user = evaluation.User!;
        var supportAccount = configuration["BootstrapSettings:SupportAccount"] ?? "support";
        return Ok(ApiResult<SessionDto>.SuccessResult(new SessionDto
        {
            UserId = user.Id,
            Account = user.Account,
            Name = user.Name,
            IsAdmin = user.IsAdmin,
            MustChangePassword = evaluation.MustChangePassword,
            IsSupportAccount = string.Equals(user.Account, supportAccount, StringComparison.Ordinal),
            PermissionKeys = (await permissionChecker.GetEffectivePermissionKeysAsync(user.Id)).Order(StringComparer.Ordinal).ToList(),
        }));
    }

    /// <summary>依目前登入者權限過濾好的側邊選單（規則與網頁側邊欄相同）。</summary>
    [HttpGet("menu")]
    public async Task<ActionResult<ApiResult<List<MenuItemDto>>>> GetMenu()
    {
        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<List<MenuItemDto>>.UnauthorizedResult("無法識別使用者身分。"));
        }

        var evaluation = await userSessionService.EvaluateAsync(userId);
        if (evaluation.LogoutReason is not null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResult<List<MenuItemDto>>.ForbiddenResult("帳號目前無法使用系統。"));
        }

        var user = evaluation.User!;
        var keys = await permissionChecker.GetEffectivePermissionKeysAsync(user.Id);
        var items = await sidebarMenuService.LoadAuthorizedMenuItemsAsync(
            name => AuthenticationStateHelper.CanAccessPage(keys, user.IsAdmin, name),
            user.IsAdmin);
        return Ok(ApiResult<List<MenuItemDto>>.SuccessResult(MenuItemMapping.ToDtos(items)));
    }

    private ObjectResult Blocked(string reason)
    {
        var result = ApiResult<SessionDto>.ForbiddenResult("帳號目前無法使用系統。");
        result.Data = new SessionDto { Status = reason };
        return StatusCode(StatusCodes.Status403Forbidden, result);
    }
}
