using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>
/// 用真的網頁登入表單取得登入 Cookie（含 antiforgery 與畫面上的驗證碼），不偽造 claims（1.1.0 起，1.1.2 抽成共用）。
/// WASM 前端打 API 與 Hub 都靠這個 Cookie。
/// </summary>
internal static partial class TestCookieLogin
{
    /// <param name="client">不能自動跟隨轉址（要看得到登入後的 302），Cookie 由它自己的處理常式保存。</param>
    public static async Task LoginAsync(HttpClient client, string account, string password)
    {
        using var response = await LoginAsync(client, account, password, "/Auths/Login");
    }

    /// <summary>從 <paramref name="loginPath"/>（可帶 ReturnUrl）登入，回傳登入後的 302 回應（1.1.58）。</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string account, string password, string loginPath)
    {
        var html = await client.GetStringAsync(loginPath);
        var fields = HiddenInputPattern().Matches(html)
            .Select(m => (Name: NamePattern().Match(m.Value).Groups[1].Value, Value: WebUtility.HtmlDecode(ValuePattern().Match(m.Value).Groups[1].Value)))
            .Where(x => x.Name.Length > 0)
            .ToDictionary(x => x.Name, x => x.Value);
        fields["Input.Account"] = account;
        fields["Input.Password"] = password;
        fields["Input.CaptchaInput"] = fields["Input.CaptchaCode"];

        var response = await client.PostAsync(loginPath, new FormUrlEncodedContent(fields));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"{account} 網頁登入失敗：{(int)response.StatusCode}");
        Assert.DoesNotContain("/Auths/Login", response.Headers.Location?.ToString() ?? string.Empty);
        return response;
    }

    /// <summary>建一個帶指定權限鍵的角色與帳號，回傳帳號與 Id。</summary>
    public static async Task<(string Account, int UserId)> CreateUserAsync(IServiceProvider services, string password, string[] permissions, bool mustChangePassword = false)
    {
        var account = $"cookie-{Guid.NewGuid():N}";
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var role = new RoleView { Name = $"Cookie 測試角色-{Guid.NewGuid():N}", TabViewJson = JsonSerializer.Serialize(permissions) };
        db.RoleView.Add(role);
        await db.SaveChangesAsync();
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Status = true,
            RoleViewId = role.Id,
            MustChangePassword = mustChangePassword,
            Password = SecurePasswordHasher.HashPassword(password),
        };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        await new RbacWriteService(db).SyncRolePermissionsAsync(role.Id, permissions);
        return (account, user.Id);
    }

    [GeneratedRegex(@"<input[^>]*type=""hidden""[^>]*>")]
    private static partial Regex HiddenInputPattern();

    [GeneratedRegex(@"name=""([^""]*)""")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"value=""([^""]*)""")]
    private static partial Regex ValuePattern();
}
