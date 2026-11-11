using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.AiUsage;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M5（1.1.36）：Token 用量 API（<c>api/v1/ai-usage</c>），只限管理者。
/// CSV、PDF 由伺服器產生；以前畫面寫的稽核改由伺服器寫（via=Api）。用假 PDF 的測試主機，PDF 也測得到。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AiUsageApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "ai-usage-api-pass";
    private const string Base = "/api/v1/ai-usage";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NonAdmins_ShouldBeRejected()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.GetAsync($"{Base}/overview"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        foreach (var request in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => client.GetAsync($"{Base}/overview"),
                     () => client.PostAsJsonAsync($"{Base}/report", new AiUsageFilter()),
                     () => client.PostAsJsonAsync($"{Base}/export/csv", new AiUsageFilter()),
                     () => client.DeleteAsync($"{Base}/1"),
                     () => client.PostAsync($"{Base}/clear-all", null),
                 })
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Overview_ReportRowsAndOptions_ShouldFollowTheFilter()
    {
        var model = Unique("model");
        var (userId, userName) = await SeedAsync(model, count: 2);
        using var client = await LoginAdminAsync();
        var filter = new AiUsageFilter(Model: model);

        var overview = (await ReadAsync<AiUsageOverviewDto>(await client.GetAsync($"{Base}/overview"))).Data!;
        Assert.NotNull(overview.Summary);
        Assert.Equal(10_000, overview.MaxExportRows);

        var report = (await ReadAsync<AiUsageReport>(await client.PostAsJsonAsync($"{Base}/report", filter))).Data!;
        Assert.Equal(2, report.CallCount);
        Assert.Equal(200, report.InputTokens);

        var page = (await ReadAsync<AiUsagePagedResult>(await client.PostAsJsonAsync($"{Base}/rows", new AiUsageQuery(filter, 1, 20)))).Data!;
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Rows, x => Assert.Equal(model, x.Model));

        var options = (await ReadAsync<AiUsageFilterOptions>(await client.GetAsync($"{Base}/filter-options"))).Data!;
        Assert.Contains(options.Users, x => x.UserId == userId && x.UserName == userName);
        Assert.Contains(model, options.Models);
    }

    [Fact]
    public async Task Detail_ShouldReturnTheRow_Or404()
    {
        var model = Unique("model");
        await SeedAsync(model, count: 1);
        using var client = await LoginAdminAsync();
        var id = await IdsOfAsync(model).ContinueWith(t => t.Result.Single());

        var detail = (await ReadAsync<AiUsageDetailResponseDto>(await client.GetAsync($"{Base}/{id}"))).Data!;
        Assert.Equal(model, detail.Detail!.Model);
        Assert.Null(detail.CallLogId);

        using var missing = await client.GetAsync($"{Base}/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ExportCsvAndPdf_ShouldBeProducedByTheServer_AndAudited()
    {
        var model = Unique("model");
        await SeedAsync(model, count: 2);
        using var client = await LoginAdminAsync();
        var filter = new AiUsageFilter(Model: model);

        using (var csv = await client.PostAsJsonAsync($"{Base}/export/csv", filter))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            var text = System.Text.Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
            Assert.Contains("呼叫識別碼", text);
            Assert.Equal(2, text.Split('\n').Count(x => x.Contains(model)));
        }

        using (var pdf = await client.PostAsJsonAsync($"{Base}/export/pdf", filter))
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync()));
        }

        using var scope = factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.TargetType == "AiUsageLog").ToListAsync();
        Assert.Contains(audits, x => x.Action == AuditActions.AiUsageExport && x.Detail!.StartsWith("format=csv; rows=2;") && x.Detail.EndsWith("via=Api"));
        Assert.Contains(audits, x => x.Action == AuditActions.AiUsageExportPdf && x.Detail!.StartsWith("rows=2;") && x.Detail.EndsWith("via=Api"));
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheRow_AndAudit()
    {
        var model = Unique("model");
        await SeedAsync(model, count: 1);
        using var client = await LoginAdminAsync();
        var id = (await IdsOfAsync(model)).Single();

        using (var deleted = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        using (var again = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        Assert.Empty(await IdsOfAsync(model));
        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.AiUsageDelete && x.TargetId == id.ToString());
        Assert.StartsWith($"feature={AiUsageFeature.AiChat};", audit.Detail);
        Assert.EndsWith("via=Api", audit.Detail);
    }

    [Fact]
    public async Task Purge_ShouldRemoveRowsBeforeTheDate_AndAudit()
    {
        var model = Unique("model");
        await SeedAsync(model, count: 2, occurredAt: new DateTime(2020, 1, 1, 9, 0, 0));
        using var client = await LoginAdminAsync();

        using var response = await client.PostAsJsonAsync($"{Base}/purge", new AiUsagePurgeRequestDto { Before = new DateTime(2020, 1, 2) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync<int>(response)).Data >= 2);
        Assert.Empty(await IdsOfAsync(model));

        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .AnyAsync(x => x.Action == AuditActions.AiUsagePurge && x.Detail!.StartsWith("before=2020-01-02;") && x.Detail.EndsWith("via=Api")));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<(int UserId, string UserName)> SeedAsync(string model, int count, DateTime? occurredAt = null)
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, []);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        for (var i = 0; i < count; i++)
        {
            db.AiUsageLog.Add(new AiUsageLog
            {
                OccurredAt = occurredAt ?? DateTime.Now.AddMinutes(-i),
                Feature = AiUsageFeature.AiChat,
                Outcome = AiUsageOutcome.Succeeded,
                Provider = "Fake",
                Model = model,
                UserId = userId,
                UserName = account,
                InputTokens = 100,
                OutputTokens = 50,
            });
        }

        await db.SaveChangesAsync();
        return (userId, account);
    }

    private async Task<List<int>> IdsOfAsync(string model)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AiUsageLog.AsNoTracking()
            .Where(x => x.Model == model).Select(x => x.Id).ToListAsync();
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
