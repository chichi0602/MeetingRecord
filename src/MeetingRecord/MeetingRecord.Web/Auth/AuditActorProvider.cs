using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// 稽核的「目前操作者」（1.0.34）。Scoped。
/// <list type="number">
/// <item>Blazor：<see cref="CurrentUserService"/>（由 <c>AuthenticationStateHelper.Check</c> 填入）。</item>
/// <item>API：CurrentUserService 是空的，改從 HttpContext 的 claims 取（<see cref="RequestActorResolver"/>，Cookie 與 JWT 的對應相反）。</item>
/// <item>都沒有（背景排程、匿名）：<c>(null, null)</c>。</item>
/// </list>
/// </summary>
public sealed class AuditActorProvider : IAuditActorProvider
{
    private readonly CurrentUserService currentUserService;
    private readonly IHttpContextAccessor httpContextAccessor;

    public AuditActorProvider(CurrentUserService currentUserService, IHttpContextAccessor httpContextAccessor)
    {
        this.currentUserService = currentUserService;
        this.httpContextAccessor = httpContextAccessor;
    }

    public (int? UserId, string? Account) GetActor()
    {
        var currentUser = currentUserService.CurrentUser;
        if (currentUser.Id > 0)
        {
            return (currentUser.Id, currentUser.Account);
        }

        var (account, userId) = RequestActorResolver.Resolve(httpContextAccessor.HttpContext?.User);
        return (userId, account);
    }
}
