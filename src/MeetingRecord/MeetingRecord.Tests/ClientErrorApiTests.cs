using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M6（1.1.47）：瀏覽器錯誤回報改走 <c>POST api/v1/client-errors</c>。
/// 登入者才能回報；記進系統例外紀錄（來源「瀏覽器」、帶帳號、頁面只留路徑）；依帳號限流。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class ClientErrorApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "client-error-api-pass";
    private const string Url = "/api/v1/client-errors";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Anonymous_ShouldBe401()
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await anonymous.PostAsJsonAsync(Url, new ClientErrorReportDto { Message = "boom" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Report_ShouldLandInTheExceptionLog_WithTheCallerAndPathOnly()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        var message = $"e2e-browser-{Guid.NewGuid():N}";

        using (var response = await client.PostAsJsonAsync(Url, new ClientErrorReportDto
               {
                   Kind = "unhandledrejection",
                   Message = message,
                   Stack = "at boom (app.js:1:2)",
                   Source = "https://site/app.js:1:2",
                   Path = "/meetings?token=secret#top",
               }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(JsonSerializer.Deserialize<ApiResult<bool>>(await response.Content.ReadAsStringAsync(), JsonOptions)!.Data);
        }

        // 系統例外紀錄由背景寫入器寫：等一下。
        for (var attempt = 0; ; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().ExceptionLog.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Message == message);
            if (row is not null)
            {
                Assert.Equal(ExceptionSources.Browser, row.Source);
                Assert.Equal(account, row.Account);
                Assert.Equal(userId, row.UserId);
                Assert.Equal("/meetings", row.Page);   // 查詢字串與錨點可能含權杖，不得進紀錄
                break;
            }

            Assert.True(attempt < 50, "等不到系統例外紀錄。");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Report_OverThePerAccountLimit_ShouldBeDropped()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);

        var results = new List<bool>();
        for (var i = 0; i < 12; i++)
        {
            using var response = await client.PostAsJsonAsync(Url, new ClientErrorReportDto { Message = $"flood-{Guid.NewGuid():N}" });
            results.Add(JsonSerializer.Deserialize<ApiResult<bool>>(await response.Content.ReadAsStringAsync(), JsonOptions)!.Data);
        }

        // 預設每個帳號每分鐘 10 筆（ClientErrorReporting:MaxPerCircuitPerMinute）。
        Assert.Equal(10, results.Count(x => x));
        Assert.False(results[^1]);
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
