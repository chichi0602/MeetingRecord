using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Sessions;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.4（WASM 遷移）：須改密碼的帳號不能用 Cookie 或 JWT 直接打 API 繞過「先改密碼」。
/// 例外：Session API（前端靠它知道該導去改密碼）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class MustChangePasswordApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "must-change-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CookieUser_WithMustChangePassword_ShouldBeBlocked_ButSessionAllowed()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_分類清單], mustChangePassword: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);

        using var blocked = await SearchCategoriesAsync(client);
        var result = await ReadAsync<object>(blocked);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains(MustChangePasswordFilter.Reason, result.Errors!["Reason"]);

        using var session = await client.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.True((await ReadAsync<SessionDto>(session)).Data!.MustChangePassword);
    }

    [Fact]
    public async Task BearerUser_MarkedAfterTokenIssued_ShouldBeBlocked_UntilPasswordChanged()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_分類清單]);
        using var client = factory.CreateClient();
        using (var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = Password }))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await ReadAsync<TokenResponseDto>(login)).Data!.AccessToken);
        }

        using (var before = await SearchCategoriesAsync(client))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // 權杖發出後管理者才重設密碼（打開標記）。
        await SetMustChangePasswordAsync(userId, true);
        using (var blocked = await SearchCategoriesAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }

        // 改完密碼（標記清掉）就恢復。
        await SetMustChangePasswordAsync(userId, false);
        using var after = await SearchCategoriesAsync(client);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    private async Task SetMustChangePasswordAsync(int userId, bool value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        await db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(x => x.SetProperty(u => u.MustChangePassword, value));
    }

    private static Task<HttpResponseMessage> SearchCategoriesAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Category/search") { Content = JsonContent.Create(new { PageIndex = 1, PageSize = 10 }) };
        request.Headers.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client.SendAsync(request);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
