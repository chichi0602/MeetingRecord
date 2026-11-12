using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Web.Hubs;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 前端的推播通道（1.1.2） <see cref="AppHub"/> 與未讀數 API。
/// 用真的網頁登入 Cookie 連 Hub（TestServer 不支援 WebSocket，改用 LongPolling）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AppHubTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "app-hub-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task LoggedInUser_ShouldReceiveOwnNotificationChanges_ButNotOthers()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, ["會議紀錄"]);
        var (_, otherUserId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, ["會議紀錄"]);
        var cookies = await LoginAsync(account);
        await using var connection = Connect(cookies);
        var received = 0;
        connection.On(AppHub.NotificationsChanged, () => Interlocked.Increment(ref received));
        await connection.StartAsync();
        var notifier = factory.Services.GetRequiredService<IUserNotificationNotifier>();

        // 別人的通知不該收到。
        notifier.Notify([otherUserId]);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref received));

        // 自己的通知要收到。
        notifier.Notify([userId]);
        await WaitUntilAsync(() => Volatile.Read(ref received) > 0);
    }

    [Fact]
    public async Task Anonymous_ShouldNotConnect()
    {
        await using var connection = Connect(new CookieContainer());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task UnreadCount_ShouldCountOnlyOwnUnreadNotifications()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, ["會議紀錄"]);
        var (_, otherUserId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, ["會議紀錄"]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            db.UserNotification.AddRange(
                new UserNotification { UserId = userId, Message = "未讀一", CreatedAt = DateTime.Now },
                new UserNotification { UserId = userId, Message = "未讀二", CreatedAt = DateTime.Now },
                new UserNotification { UserId = userId, Message = "已讀", CreatedAt = DateTime.Now, ReadAt = DateTime.Now },
                new UserNotification { UserId = otherUserId, Message = "別人的", CreatedAt = DateTime.Now });
            await db.SaveChangesAsync();
        }

        var cookies = await LoginAsync(account);
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var response = await client.GetAsync("/api/v1/notifications/unread-count");
        var result = JsonSerializer.Deserialize<ApiResult<int>>(await response.Content.ReadAsStringAsync(), JsonOptions)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, result.Data);
    }

    private async Task<CookieContainer> LoginAsync(string account)
    {
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        await TestCookieLogin.LoginAsync(client, account, Password);
        return cookies;
    }

    private HubConnection Connect(CookieContainer cookies)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, AppHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => new CookieContainerHandler(cookies) { InnerHandler = factory.Server.CreateHandler() };
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等了 10 秒還沒收到推播");
            await Task.Delay(50);
        }
    }
}
