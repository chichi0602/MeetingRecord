using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Web;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Tests;

/// <summary>
/// API 授權守門（1.0.58）：掃描 Web 組件所有 Controller 的每一個 action，
/// 沒有授權屬性的端點一律讓測試變紅。WASM 遷移要新增上百支 API，這道防線先建好。
/// <para>
/// 每個 action 必須是下列其一：
/// 1. 在 <see cref="AnonymousAllowList"/>（明列可匿名的端點）；
/// 2. 有 <see cref="AuthorizeAttribute"/>（類別或方法）**且**有 <see cref="HasPermissionAttribute"/>；
/// 3. 有 <see cref="AuthorizeAttribute"/> 且在 <see cref="AuthenticatedOnlyAllowList"/>（只需登入、不需動作權限）。
/// 白名單每一項都要寫理由；不要為了讓測試變綠就把端點加進去。
/// </para>
/// </summary>
public sealed class ApiAuthorizationConventionTests
{
    /// <summary>可以匿名呼叫的端點。<c>*</c> 表示整個 Controller。</summary>
    private static readonly Dictionary<string, string> AnonymousAllowList = new(StringComparer.Ordinal)
    {
        ["AuthController.Login"] = "登入本身，呼叫時還沒有權杖",
        ["AuthController.Refresh"] = "用 refresh token 換新權杖，access token 可能已過期",
        ["ExternalAuthController.*"] = "Google OAuth 導向與回呼，屬網頁 Cookie 登入流程，不是 API",
    };

    /// <summary>只需登入、不檢查動作權限的端點。</summary>
    private static readonly Dictionary<string, string> AuthenticatedOnlyAllowList = new(StringComparer.Ordinal)
    {
        ["AuthController.Me"] = "回傳呼叫者自己的帳號資料，任何登入者都可以看自己",
    };

    [Fact]
    public void EveryControllerAction_ShouldBeAnonymousByAllowList_OrRequirePermission()
    {
        var violations = new List<string>();

        foreach (var action in DiscoverActions())
        {
            var key = $"{action.Controller.Name}.{action.Method.Name}";
            var controllerWildcard = $"{action.Controller.Name}.*";
            var isAnonymousAllowed = AnonymousAllowList.ContainsKey(key) || AnonymousAllowList.ContainsKey(controllerWildcard);

            var hasAllowAnonymous = action.Has<AllowAnonymousAttribute>();
            var hasAuthorize = action.Has<AuthorizeAttribute>();

            if (hasAllowAnonymous || !hasAuthorize)
            {
                if (!isAnonymousAllowed)
                {
                    violations.Add($"{key}：可以匿名呼叫（{(hasAllowAnonymous ? "有 [AllowAnonymous]" : "沒有 [Authorize]")}），但不在匿名白名單");
                }

                continue;
            }

            if (isAnonymousAllowed)
            {
                violations.Add($"{key}：在匿名白名單裡卻要求登入，白名單過時了，請移除");
                continue;
            }

            if (!action.Has<HasPermissionAttribute>() && !AuthenticatedOnlyAllowList.ContainsKey(key))
            {
                violations.Add($"{key}：有 [Authorize] 但沒有 [HasPermission]，也不在「只需登入」白名單");
            }
        }

        Assert.True(violations.Count == 0, "以下端點的授權設定不符合慣例：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void EveryAuthorizeAttribute_ShouldNameItsAuthenticationScheme()
    {
        // 預設 scheme 是網頁登入 Cookie；沒寫 AuthenticationSchemes 的 [Authorize] 會靜靜地改用 Cookie 驗證，
        // 外部用戶端拿 JWT 打進來反而 401，或者讓 API 意外接受瀏覽器的 Cookie（CSRF 風險）。
        var violations = DiscoverActions()
            .SelectMany(action => action.Attributes<AuthorizeAttribute>()
                .Where(attribute => string.IsNullOrWhiteSpace(attribute.AuthenticationSchemes))
                .Select(_ => $"{action.Controller.Name}.{action.Method.Name}"))
            .Distinct()
            .ToList();

        Assert.True(violations.Count == 0, "以下端點的 [Authorize] 沒有指定 AuthenticationSchemes：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void AllowLists_ShouldOnlyNameExistingActions()
    {
        var actions = DiscoverActions().ToList();
        var known = actions.Select(a => $"{a.Controller.Name}.{a.Method.Name}")
            .Concat(actions.Select(a => $"{a.Controller.Name}.*"))
            .ToHashSet(StringComparer.Ordinal);

        var stale = AnonymousAllowList.Keys.Concat(AuthenticatedOnlyAllowList.Keys)
            .Where(key => !known.Contains(key))
            .ToList();

        Assert.True(stale.Count == 0, "白名單裡有已經不存在的端點，請移除：\n" + string.Join("\n", stale));
    }

    [Fact]
    public void Discovery_ShouldFindTheKnownControllers()
    {
        // 防止掃描本身壞掉（例如組件換了名字）時，上面的測試因為「什麼都沒掃到」而白白通過。
        var controllers = DiscoverActions().Select(a => a.Controller.Name).ToHashSet();

        Assert.Contains("AuthController", controllers);
        Assert.Contains("ProjectController", controllers);
        Assert.Contains("MeetingController", controllers);
        Assert.True(DiscoverActions().Count() >= 30);
    }

    [Fact]
    public void TemplateWeatherForecastController_ShouldStayDeleted()
    {
        // 1.0.58 刪除：模板遺留的 /WeatherForecast 沒有任何驗證，任何人都能呼叫。
        Assert.DoesNotContain(DiscoverActions(), a => a.Controller.Name == "WeatherForecastController");
    }

    private static IEnumerable<ControllerAction> DiscoverActions()
        => typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null)
                .Select(method => new ControllerAction(controller, method)));

    private sealed record ControllerAction(Type Controller, MethodInfo Method)
    {
        public bool Has<TAttribute>() where TAttribute : Attribute => Attributes<TAttribute>().Any();

        public IEnumerable<TAttribute> Attributes<TAttribute>() where TAttribute : Attribute
            => Method.GetCustomAttributes<TAttribute>(inherit: true)
                .Concat(Controller.GetCustomAttributes<TAttribute>(inherit: true));
    }
}
