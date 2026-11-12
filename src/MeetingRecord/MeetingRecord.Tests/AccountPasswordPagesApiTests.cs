using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 變更密碼頁與設定 API 密碼頁的 API（1.1.13）。規則在服務層（<c>ChangeOwnPasswordAsync</c>、<c>SetOwnApiPasswordAsync</c>）：
/// 要確認新密碼、稽核 <c>via=FirstLogin</c>；須改密碼的帳號也能改，改完標記清掉；沒有本地密碼的帳號設定 API 密碼免驗目前密碼。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AccountPasswordPagesApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "account-pages-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ChangeOwnPassword_ShouldFollowThePageRules_AndClearMustChange()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄], mustChangePassword: true);
        using var client = await LoginAsync(account);

        await AssertRejectedAsync(await Post(client, "change-own-password", "wrong-pass", "new-pass-1", "new-pass-1"), "目前密碼不正確。");
        await AssertRejectedAsync(await Post(client, "change-own-password", Password, "new-pass-1", "different"), "新密碼與確認密碼不一致。");

        // 須改密碼的帳號也能改（就是要它改），沒有字數下限（ChangeOwnPasswordAsync 的規則）。
        using (var ok = await Post(client, "change-own-password", Password, "abc", "abc"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False((await db.MyUser.AsNoTracking().SingleAsync(x => x.Id == userId)).MustChangePassword);
        var audit = await db.AuditLog.AsNoTracking().Where(x => x.Action == AuditActions.PasswordChange && x.TargetId == userId.ToString() && x.Success).SingleAsync();
        Assert.Contains("via=FirstLogin", audit.Detail);
    }

    [Fact]
    public async Task ChangeOwnPassword_SupportAccount_ShouldBeRejected()
    {
        using var client = await LoginAsync("support", "support");
        await AssertRejectedAsync(await Post(client, "change-own-password", "support", "x-pass", "x-pass"), "系統預設開發帳號 support 禁止變更密碼。");
    }

    [Fact]
    public async Task ApiPassword_ShouldVerifyCurrentOnlyWhenThereIsALocalPassword()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);

        Assert.True((await ReadAsync<PasswordStatusDto>(await client.GetAsync("/api/v1/account/password-status"))).Data!.HasLocalPassword);
        await AssertRejectedAsync(await Post(client, "api-password", "wrong-pass", "api-pass-1", "api-pass-1"), "目前密碼不正確。");
        using (var ok = await Post(client, "api-password", Password, "api-pass-1", "api-pass-1"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 沒有本地密碼（例如 Google 帳號）：免填目前密碼。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            (await db.MyUser.SingleAsync(x => x.Id == userId)).Password = string.Empty;
            await db.SaveChangesAsync();
        }

        Assert.False((await ReadAsync<PasswordStatusDto>(await client.GetAsync("/api/v1/account/password-status"))).Data!.HasLocalPassword);
        using (var ok = await Post(client, "api-password", null, "api-pass-2", "api-pass-2"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 新設定的 API 密碼可以拿到權杖。
        using var anonymous = factory.CreateClient();
        using var login = await anonymous.PostAsJsonAsync("/api/Auth/login", new MeetingRecord.Dtos.Auths.LoginRequestDto { Account = account, Password = "api-pass-2" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string action, string? current, string newPassword, string confirm)
        => client.PostAsJsonAsync($"/api/v1/account/{action}", new OwnPasswordRequestDto { CurrentPassword = current, NewPassword = newPassword, ConfirmPassword = confirm });

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string message)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(message, (await ReadAsync<object>(response)).ErrorMessage);
        }
    }

    private async Task<HttpClient> LoginAsync(string account, string password = Password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
