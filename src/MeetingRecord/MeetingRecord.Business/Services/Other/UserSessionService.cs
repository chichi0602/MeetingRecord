using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

/// <summary>登入者目前能不能用系統（1.1.0）。</summary>
public enum UserSessionStatus
{
    Active,
    Deleted,
    Disabled,
    NoRole,
}

/// <param name="Status">帳號狀態。</param>
/// <param name="User">帳號資料；<see cref="UserSessionStatus.Deleted"/> 時為 null。</param>
/// <param name="MustChangePassword">要先改密碼才能用（管理者建立／重設過密碼，或仍是預設密碼）。</param>
public sealed record UserSessionEvaluation(UserSessionStatus Status, MyUserAdapterModel? User, bool MustChangePassword)
{
    /// <summary>被擋下時導向登出頁帶的原因（<see cref="LogoutReasons"/>）；可以用時為 null。</summary>
    public string? LogoutReason => Status switch
    {
        UserSessionStatus.Deleted => LogoutReasons.Deleted,
        UserSessionStatus.Disabled => LogoutReasons.Disabled,
        UserSessionStatus.NoRole => LogoutReasons.NoRole,
        _ => null,
    };
}

/// <summary>
/// 判斷登入者目前的狀態（1.1.0，WASM 遷移 M1）：刪除、停用、沒有角色、需要改密碼。
///
/// <para>
/// 從 <see cref="AuthenticationStateHelper.Check"/> 拆出來：那邊判斷完直接用 NavigationManager 導向，只能給 Blazor Server 頁用；
/// WASM 前端改由 Session API 取得同一份判斷、自己導向。兩邊共用這一份規則，不會各說各話。
/// </para>
/// </summary>
public sealed class UserSessionService
{
    private readonly MyUserService myUserService;

    public UserSessionService(MyUserService myUserService)
    {
        this.myUserService = myUserService;
    }

    public async Task<UserSessionEvaluation> EvaluateAsync(int userId)
    {
        var user = await myUserService.GetAsync(userId);
        if (user.Id == 0)
        {
            return new UserSessionEvaluation(UserSessionStatus.Deleted, null, false);
        }

        if (!user.Status)
        {
            return new UserSessionEvaluation(UserSessionStatus.Disabled, user, false);
        }

        var mustChangePassword = await myUserService.NeedChangePasswordAsync(user);
        var status = user.RoleView is null ? UserSessionStatus.NoRole : UserSessionStatus.Active;
        return new UserSessionEvaluation(status, user, mustChangePassword);
    }
}
