using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Accounts;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Dtos.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M2（1.1.6）：版面需要的 API——通知清單與標已讀、變更密碼、關於、頁面拒絕稽核。
/// 用真的網頁登入 Cookie 呼叫（WASM 前端的路徑）；寫入請求都帶防偽標頭。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class LayoutApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "layout-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Notifications_ListAndMarkRead_ShouldOnlyTouchOwn()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        var (_, otherId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        var (mineA, mineB, others) = await SeedNotificationsAsync(userId, otherId);
        using var client = await LoginAsync(account);

        var list = (await ReadAsync<List<NotificationDto>>(await client.GetAsync("/api/v1/notifications"))).Data!;
        Assert.Equal([mineB, mineA], list.Select(x => x.Id));

        // 標別人的那一則：照樣回成功（不透露存不存在），但不能動到。
        using (var foreign = await PostAsync(client, $"/api/v1/notifications/{others}/read"))
        {
            Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        }

        Assert.Null(await ReadAtAsync(others));

        using (await PostAsync(client, $"/api/v1/notifications/{mineA}/read"))
        {
            Assert.NotNull(await ReadAtAsync(mineA));
            Assert.Null(await ReadAtAsync(mineB));
        }

        using (await PostAsync(client, "/api/v1/notifications/read-all"))
        {
            Assert.NotNull(await ReadAtAsync(mineB));
            Assert.Null(await ReadAtAsync(others));
        }
    }

    [Fact]
    public async Task ChangePassword_Rules_ShouldMatchServerDialog()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄], mustChangePassword: true);
        using var client = await LoginAsync(account);

        using (var wrong = await PostJsonAsync(client, "/api/v1/account/change-password", new ChangePasswordRequestDto { CurrentPassword = "wrong-pass", NewPassword = "new-pass-123" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("目前密碼輸入錯誤。", (await ReadAsync<object>(wrong)).ErrorMessage);
        }

        using (var tooShort = await PostJsonAsync(client, "/api/v1/account/change-password", new ChangePasswordRequestDto { CurrentPassword = Password, NewPassword = "12345" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        }

        // 須改密碼的帳號也能改（就是要它改），改完標記清掉。
        using (var ok = await PostJsonAsync(client, "/api/v1/account/change-password", new ChangePasswordRequestDto { CurrentPassword = Password, NewPassword = "new-pass-123" }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            Assert.False((await db.MyUser.AsNoTracking().SingleAsync(x => x.Id == userId)).MustChangePassword);
        }

        // 新密碼登得進去。
        using var relogin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(relogin, account, "new-pass-123");
    }

    [Fact]
    public async Task ChangePassword_SupportAccount_ShouldBeForbidden()
    {
        using var client = await LoginAsync("support", "support");

        using var response = await PostJsonAsync(client, "/api/v1/account/change-password", new ChangePasswordRequestDto { CurrentPassword = "support", NewPassword = "new-pass-123" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task About_ShouldReturnConfiguredVersion()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);

        var about = (await ReadAsync<SystemAboutDto>(await client.GetAsync("/api/v1/system/about"))).Data!;

        var configured = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MeetingRecord.Models.Systems.SystemSettings>>().Value.SystemInformation;
        Assert.Equal(configured.SystemVersion, about.SystemVersion);
        Assert.Equal(configured.SystemName, about.SystemName);
        Assert.True(about.Uptime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task PageDenied_ShouldWriteAuditLikeServerPages_AndRejectOddRoutes()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);

        using (var ok = await PostJsonAsync(client, "/api/v1/audit/page-denied", new PageDeniedRequestDto { Route = "/categories" }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var row = await db.AuditLog.AsNoTracking().Where(x => x.ActorUserId == userId && x.Action == AuditActions.PermissionDenied).SingleAsync();
            Assert.Equal("Page", row.TargetType);
            Assert.Equal("/categories", row.TargetId);
            Assert.Contains($"via={AuditVia.Wasm}", row.Detail);
        }

        foreach (var bad in new[] { "categories", "/a b", "/<script>", "/" + new string('a', 201) })
        {
            using var rejected = await PostJsonAsync(client, "/api/v1/audit/page-denied", new PageDeniedRequestDto { Route = bad });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    private async Task<(int MineA, int MineB, int Others)> SeedNotificationsAsync(int userId, int otherId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var a = new UserNotification { UserId = userId, Message = "較早的", CreatedAt = DateTime.Now.AddMinutes(-5) };
        var b = new UserNotification { UserId = userId, Message = "較新的", CreatedAt = DateTime.Now };
        var o = new UserNotification { UserId = otherId, Message = "別人的", CreatedAt = DateTime.Now };
        db.UserNotification.AddRange(a, b, o);
        await db.SaveChangesAsync();
        return (a.Id, b.Id, o.Id);
    }

    private async Task<DateTime?> ReadAtAsync(int notificationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        return (await db.UserNotification.AsNoTracking().SingleAsync(x => x.Id == notificationId)).ReadAt;
    }

    private async Task<HttpClient> LoginAsync(string account, string password = Password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url) => client.PostAsync(url, content: null);

    private static Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string url, T body) => client.PostAsJsonAsync(url, body);

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
