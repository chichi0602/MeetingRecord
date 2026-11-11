using System.Net;
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
/// WASM 遷移 M5：監控與診斷 API（<c>api/v1/monitoring</c>），只限管理者。
/// 1.1.32：資料庫用量、檔案儲存用量。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class MonitoringApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "monitoring-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Endpoints => new()
    {
        "/api/v1/monitoring/database-usage",
        "/api/v1/monitoring/storage-usage",
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task NonAdmins_ShouldBeRejected_AndAudited(string url)
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 就算有其他頁的全部權限也不行：這些頁的權限鍵刻意不在角色權限目錄裡，只看是不是管理者。
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄, MagicObjectHelper.角色_使用說明]);
        using var client = await LoginAsync(account);
        using (var response = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var denied = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.PermissionDenied && x.ActorUserId == userId);
        Assert.Contains("管理者", denied.Detail);
    }

    [Fact]
    public async Task DatabaseUsage_ShouldListTheTables()
    {
        using var client = await LoginAdminAsync();

        using var response = await client.GetAsync("/api/v1/monitoring/database-usage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await ReadAsync<DatabaseUsageReport>(response)).Data!;
        Assert.Equal(string.Empty, report.ErrorMessage);
        Assert.True(report.MainDbBytes > 0);
        Assert.True(report.PageSize > 0);
        Assert.Contains(report.Tables, x => x.TableName == "MyUser" && x.RowCount > 0);
    }

    [Fact]
    public async Task StorageUsage_ShouldListTheConfiguredDirectories()
    {
        using var client = await LoginAdminAsync();

        using var response = await client.GetAsync("/api/v1/monitoring/storage-usage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await ReadAsync<StorageUsageReport>(response)).Data!;
        Assert.NotEmpty(report.Directories);
        Assert.All(report.Directories, x => Assert.False(string.IsNullOrWhiteSpace(x.Label)));
        Assert.NotNull(report.Warnings);
    }

    private async Task<HttpClient> LoginAdminAsync()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, []);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser
                .Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, true));
        }

        return await LoginAsync(account);
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
