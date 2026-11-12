using System.Security.Claims;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Web.Auth;

/// <summary>
/// AI 呼叫的觸發者（1.1.28）。Scoped。
/// <list type="number">
/// <item>背景工作：<see cref="CurrentUserService"/>（背景工作自開 scope 時把上傳者填進去）。</item>
/// <item>API：CurrentUserService 是空的，改從 claims 取（<see cref="RequestActorResolver"/>）。
/// 顯示名稱的優先序同 <see cref="AiUsageAttribution"/>：姓名 → 帳號。Cookie 的 <c>Name</c> 是姓名；
/// JWT 的 <c>Name</c> 是帳號、沒有姓名，就記帳號。</item>
/// <item>都沒有：<c>(null, null)</c>。</item>
/// </list>
/// </summary>
public sealed class AiCallerProvider(CurrentUserService currentUserService, IHttpContextAccessor httpContextAccessor) : IAiCallerProvider
{
    public (int? UserId, string? UserName) GetCaller()
    {
        var current = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
        if (current.UserId is not null)
        {
            return current;
        }

        var principal = httpContextAccessor.HttpContext?.User;
        var (account, userId) = RequestActorResolver.Resolve(principal);
        if (userId is null)
        {
            return (null, null);
        }

        var name = principal!.FindFirst(ClaimTypes.Sid) is not null ? principal.FindFirst(ClaimTypes.Name)?.Value : null;
        return (userId, !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(account) ? account : null);
    }
}
