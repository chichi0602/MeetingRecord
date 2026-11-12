using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Filters;

/// <summary>
/// AI 問答 API 的動作權限（1.1.21）：要的權限依路由的範圍而定，所以不能用固定的 <see cref="HasPermissionAttribute"/>。
/// <list type="bullet">
/// <item><c>project</c>（整個專案）：專案項目的該動作。</item>
/// <item><c>meeting</c>（單一會議）：會議紀錄**或**專案項目的該動作——會議頁開的問答看會議紀錄的權限，
/// 專案頁的會議歷史開的看專案項目的權限，兩個入口原本就各自判斷（畫面的 <c>CanEdit</c> 等參數）。</item>
/// </list>
/// 401／403／拒絕稽核與 <see cref="HasPermissionAttribute"/> 同一份。資料範圍（看不看得到那個專案或會議）由服務層檢查。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AiChatPermissionAttribute(string action) : Attribute, IAsyncAuthorizationFilter
{
    public const string ScopeRouteKey = "scope";

    public string Action { get; } = action;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
        => HasPermissionAttribute.AuthorizeAsync(context, KeysFor(context.RouteData.Values[ScopeRouteKey] as string, Action));

    public static string[] KeysFor(string? scope, string action) => string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
        ? [PermissionKey.For(MagicObjectHelper.角色_專案項目, action)]
        : [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, action), PermissionKey.For(MagicObjectHelper.角色_專案項目, action)];
}
