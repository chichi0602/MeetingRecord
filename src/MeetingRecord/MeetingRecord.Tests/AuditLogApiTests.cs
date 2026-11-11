using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.AuditLogs;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M5（1.1.43）：稽核紀錄 API（<c>api/v1/audit-logs</c>），只限管理者。
/// 匯出與清除的稽核由伺服器寫（via=Api），清除類在刪除<b>之後</b>才寫，這筆會留在清空後的表裡。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AuditLogApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "audit-log-api-pass";
    private const string Base = "/api/v1/audit-logs";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NonAdmins_ShouldBeRejected()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.GetAsync($"{Base}/meta"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        foreach (var request in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => client.GetAsync($"{Base}/meta"),
                     () => client.GetAsync($"{Base}/actions"),
                     () => client.PostAsJsonAsync($"{Base}/search", new AuditLogQuery()),
                     () => client.PostAsJsonAsync($"{Base}/export/csv", new AuditLogQuery()),
                     () => client.PostAsync($"{Base}/purge", null),
                     () => client.PostAsync($"{Base}/clear-all", null),
                 })
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task SearchActionsAndCsv_ShouldFollowTheQuery_AndAuditTheExport()
    {
        var action = $"Test.{Guid.NewGuid():N}"[..20];
        await SeedAsync(action, DateTime.UtcNow, detail: "第一筆");
        await SeedAsync(action, DateTime.UtcNow, detail: "第二筆", success: false);
        var (client, adminId) = await LoginAdminAsync();

        var meta = (await ReadAsync<AuditLogMetaDto>(await client.GetAsync($"{Base}/meta"))).Data!;
        Assert.Equal(10_000, meta.MaxExportRows);
        Assert.Contains(action, (await ReadAsync<List<string>>(await client.GetAsync($"{Base}/actions"))).Data!);

        var page = (await ReadAsync<PagedResult<AuditLogAdapterModel>>(await client.PostAsJsonAsync($"{Base}/search", new AuditLogQuery { Action = action, Success = false }))).Data!;
        var row = Assert.Single(page.Items);
        Assert.Equal("第二筆", row.Detail);
        Assert.Equal("失敗", row.SuccessText);

        using (var csv = await client.PostAsJsonAsync($"{Base}/export/csv", new AuditLogQuery { Action = action }))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
            Assert.Contains("第一筆", text);
            Assert.Contains("第二筆", text);
        }

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.AuditExport && x.ActorUserId == adminId);
        Assert.Equal("AuditLog", audit.TargetType);
        Assert.Equal("format=csv; rows=2; via=Api", audit.Detail);
    }

    [Fact]
    public async Task PurgeAndClearAll_ShouldAuditAfterDeleting()
    {
        var oldAction = $"Old.{Guid.NewGuid():N}"[..20];
        await SeedAsync(oldAction, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (client, adminId) = await LoginAdminAsync();

        using (var purged = await client.PostAsync($"{Base}/purge", null))
        {
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            Assert.False(await db.AuditLog.AnyAsync(x => x.Action == oldAction));
            Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.AuditPurge && x.ActorUserId == adminId && x.Detail!.EndsWith("via=Api")));
        }

        using (var cleared = await client.PostAsync($"{Base}/clear-all", null))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }

        // 清空之後只剩「清空」自己這一筆：稽核在刪除之後才寫。
        using var check = factory.Services.CreateScope();
        var remaining = await check.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking().ToListAsync();
        var only = Assert.Single(remaining);
        Assert.Equal(AuditActions.AuditClearAll, only.Action);
        Assert.Equal(adminId, only.ActorUserId);
        Assert.EndsWith("via=Api", only.Detail);
    }

    private async Task SeedAsync(string action, DateTime occurredAtUtc, string? detail = null, bool success = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        db.AuditLog.Add(new AuditLog { Action = action, OccurredAt = occurredAtUtc, Detail = detail, Success = success, TargetType = "Test" });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, int UserId)> LoginAdminAsync()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, []);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser
                .Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, true));
        }

        return (await LoginAsync(account), userId);
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
                   ?? throw new InvalidOperationException("回應不是 ApiResult");
        }
    }
}
