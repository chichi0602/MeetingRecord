using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.AiCallLogs;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M5（1.1.38）：AI 對話紀錄 API（<c>api/v1/ai-call-logs</c>），只限管理者。
/// 查看明細也寫稽核；JSON、PDF、CSV 由伺服器產生並寫稽核（via=Api）。用假 PDF 的測試主機。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AiCallLogApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "ai-call-log-api-pass";
    private const string Base = "/api/v1/ai-call-logs";
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
                     () => client.PostAsJsonAsync($"{Base}/search", new AiCallLogQuery()),
                     () => client.GetAsync($"{Base}/1"),
                     () => client.DeleteAsync($"{Base}/1"),
                     () => client.PostAsync($"{Base}/clear-all", null),
                 })
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Search_AndFilterOptions_ShouldFollowTheQuery()
    {
        var model = Unique("model");
        await RecordAsync(model, userId: 77, userName: "王對話");
        await RecordAsync(model, userId: 77, userName: "王對話");
        using var client = await LoginAdminAsync();

        var page = (await ReadAsync<PagedResult<AiCallLogAdapterModel>>(await client.PostAsJsonAsync($"{Base}/search", new AiCallLogQuery { Model = model }))).Data!;
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, x => Assert.Equal(model, x.Model));

        var options = (await ReadAsync<AiCallLogFilterOptions>(await client.GetAsync($"{Base}/filter-options"))).Data!;
        Assert.Contains(new AiCallLogUserOption(77, "王對話"), options.Users);
        Assert.Contains(model, options.Models);

        var meta = (await ReadAsync<AiCallLogMetaDto>(await client.GetAsync($"{Base}/meta"))).Data!;
        Assert.Equal(10_000, meta.MaxExportRows);
    }

    [Fact]
    public async Task Detail_ShouldReturnTheContent_AndAuditTheView()
    {
        var model = Unique("model");
        var callId = await RecordAsync(model);
        using var client = await LoginAdminAsync();

        var id = (await ReadAsync<int>(await client.GetAsync($"{Base}/by-call-id/{callId}"))).Data;
        using (var unknown = await client.GetAsync($"{Base}/by-call-id/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        var detail = (await ReadAsync<AiCallLogDetail>(await client.GetAsync($"{Base}/{id}"))).Data!;
        Assert.Equal(callId, detail.Row.CallId);
        Assert.Equal("請整理這段會議", detail.Content!.Request.Messages.Single(x => x.Role == "user").Content);
        Assert.Equal("整理好的會議紀錄", detail.Content.Response.Text);
        Assert.Contains(AiCallLogBlocks.Build(detail, AiCallLogViewMode.Conversation), x => x.IsAssistant);

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.AiCallLogView && x.TargetId == id.ToString());
        Assert.Equal($"callId={callId:N}; feature={AiUsageFeature.AiChat}; via=Api", audit.Detail);
    }

    [Fact]
    public async Task JsonPdfAndCsv_ShouldBeProducedByTheServer_AndAudited()
    {
        var model = Unique("model");
        var callId = await RecordAsync(model);
        using var client = await LoginAdminAsync();
        var id = (await ReadAsync<int>(await client.GetAsync($"{Base}/by-call-id/{callId}"))).Data;

        using (var json = await client.GetAsync($"{Base}/{id}/json"))
        {
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
            Assert.Contains("整理好的會議紀錄", await json.Content.ReadAsStringAsync());
        }

        using (var pdf = await client.PostAsync($"{Base}/{id}/pdf", null))
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        }

        using (var csv = await client.PostAsJsonAsync($"{Base}/export/csv", new AiCallLogQuery { Model = model }))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            var text = System.Text.Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
            Assert.Contains(callId.ToString(), text);
            Assert.DoesNotContain("整理好的會議紀錄", text);
        }

        using var scope = factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.TargetType == "AiCallLog").ToListAsync();
        Assert.Contains(audits, x => x.Action == AuditActions.AiCallLogDownloadJson && x.TargetId == id.ToString() && x.Detail!.EndsWith("via=Api"));
        Assert.Contains(audits, x => x.Action == AuditActions.AiCallLogExportPdf && x.TargetId == id.ToString() && x.Detail!.EndsWith("via=Api"));
        Assert.Contains(audits, x => x.Action == AuditActions.AiCallLogExport && x.Detail!.StartsWith("format=csv; rows=1;"));
    }

    [Fact]
    public async Task DeleteAndPurge_ShouldRemoveRows_AndAudit()
    {
        var model = Unique("model");
        var callId = await RecordAsync(model);
        var oldModel = Unique("old");
        await RecordAsync(oldModel, occurredAt: new DateTime(2020, 1, 1, 9, 0, 0));
        using var client = await LoginAdminAsync();
        var id = (await ReadAsync<int>(await client.GetAsync($"{Base}/by-call-id/{callId}"))).Data;

        using (var deleted = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        using (var again = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var purged = await client.PostAsJsonAsync($"{Base}/purge", new AiCallLogPurgeRequestDto { Before = new DateTime(2020, 1, 2) }))
        {
            Assert.True((await ReadAsync<int>(purged)).Data >= 1);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False(await db.AiCallLog.AnyAsync(x => x.Model == model || x.Model == oldModel));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.AiCallLogDelete && x.TargetId == id.ToString() && x.Detail!.EndsWith("via=Api")));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.AiCallLogPurge && x.Detail!.StartsWith("before=2020-01-02;")));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>經真的 <see cref="AiCallLogService.RecordAsync"/> 寫一筆（資料列＋內容檔）。</summary>
    private async Task<Guid> RecordAsync(string model, int? userId = null, string? userName = null, DateTime? occurredAt = null)
    {
        var callId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiCallLogService>().RecordAsync(new AiCallLogEntry
        {
            CallId = callId,
            OccurredAt = occurredAt ?? DateTime.Now,
            Feature = AiUsageFeature.AiChat,
            FeatureText = "AI 問答",
            Outcome = AiUsageOutcome.Succeeded,
            Provider = "Fake",
            Model = model,
            UserId = userId,
            UserName = userName,
            RelatedInfo = "測試",
            Request = new AiCallLogRequest { Messages = [new AiCallLogMessage { Role = "system", Content = "你是助理" }, new AiCallLogMessage { Role = "user", Content = "請整理這段會議" }] },
            Response = new AiCallLogResponse { Text = "整理好的會議紀錄" },
            ElapsedMilliseconds = 1200,
        });
        return callId;
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
    {
        using (response)
        {
            return JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
                   ?? throw new InvalidOperationException("回應不是 ApiResult");
        }
    }
}
