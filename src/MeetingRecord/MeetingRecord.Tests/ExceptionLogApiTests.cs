using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Testing;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M5（1.1.40）：系統例外 API（<c>api/v1/exception-logs</c>），只限管理者。
/// CSV、刪除、清除、AI 分析與 PDF 的稽核由伺服器寫（via=Api）；AI 分析送出的內容由伺服器依例外編號組、不含帳號。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class ExceptionLogApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "exception-log-api-pass";
    private const string Base = "/api/v1/exception-logs";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NonAdmins_ShouldBeRejected()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.GetAsync($"{Base}/meta"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        foreach (var request in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => client.GetAsync($"{Base}/meta"),
                     () => client.PostAsJsonAsync($"{Base}/search", new ExceptionLogQuery()),
                     () => client.GetAsync($"{Base}/1/stack-trace"),
                     () => client.DeleteAsync($"{Base}/1"),
                     () => client.PostAsync($"{Base}/clear-all", null),
                     () => client.PostAsync($"{Base}/1/ai-analysis", null),
                 })
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AiUsageLog.AnyAsync(x => x.UserId == userId));
    }

    [Fact]
    public async Task SearchStackTraceAndCsv_ShouldFollowTheQuery_AndAuditTheExport()
    {
        var message = Unique("清單");
        var id = await RecordAsync(message);
        using var client = await LoginAdminAsync();

        var meta = (await ReadAsync<ExceptionLogMetaDto>(await client.GetAsync($"{Base}/meta"))).Data!;
        Assert.Equal(ExceptionLogService.MaxRows, meta.MaxRows);
        Assert.True(meta.AiAvailable);

        var page = (await ReadAsync<PagedResult<ExceptionLogAdapterModel>>(await client.PostAsJsonAsync($"{Base}/search", new ExceptionLogQuery { Keyword = message }))).Data!;
        var row = Assert.Single(page.Items);
        Assert.Equal(id, row.Id);
        Assert.Equal("NullReferenceException", row.ShortExceptionType);

        var stack = (await ReadAsync<ExceptionLogStackTraceDto>(await client.GetAsync($"{Base}/{id}/stack-trace"))).Data!;
        Assert.Contains("at MeetingRecord.Demo", stack.StackTrace);
        Assert.Equal(AiExceptionPromptBuilder.Build(row, stack.StackTrace).Length, stack.AiPromptChars);
        using (var unknown = await client.GetAsync($"{Base}/999999/stack-trace"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        var many = (await ReadAsync<Dictionary<int, string?>>(await client.PostAsJsonAsync($"{Base}/stack-traces", new ExceptionLogStackTracesRequestDto { Ids = [id, 999999] }))).Data!;
        Assert.Contains("at MeetingRecord.Demo", many[id]);
        Assert.Null(many[999999]);

        using (var csv = await client.PostAsJsonAsync($"{Base}/export/csv", new ExceptionLogQuery { Keyword = message }))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Contains(message, System.Text.Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync()));
        }

        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog
            .AnyAsync(x => x.Action == AuditActions.ExceptionLogExport && x.Detail == "format=csv; rows=1; via=Api"));
    }

    [Fact]
    public async Task AiAnalysis_ShouldStreamFromAServerBuiltPrompt_WithoutTheAccount_AndAudit()
    {
        var message = Unique("分析");
        var id = await RecordAsync(message, account: "secret-account");
        var (client, adminId) = await LoginAdminWithIdAsync();

        using var response = await client.PostAsync($"{Base}/{id}/ai-analysis", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var events = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<DiagnosticAiStreamEventDto>(line, JsonOptions)!)
            .ToList();
        Assert.Contains(events, x => x.Type == DiagnosticAiStreamEventDto.Delta);
        var done = events[^1];
        Assert.Equal(DiagnosticAiStreamEventDto.Done, done.Type);
        Assert.Contains(E2EFakeAi.AnswerMarker, done.Content);
        Assert.Equal("fake", done.Model);
        Assert.Contains(done.Meta!, x => x.Label == "例外類型" && x.Value == "System.NullReferenceException");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var usage = await db.AiUsageLog.AsNoTracking().SingleAsync(x => x.UserId == adminId);
            Assert.Equal(AiUsageFeature.ExceptionAnalysis, usage.Feature);
            Assert.Equal(AiUsageOutcome.Succeeded, usage.Outcome);

            var callLogs = scope.ServiceProvider.GetRequiredService<AiCallLogService>();
            var detail = (await callLogs.GetDetailAsync((await callLogs.FindIdByCallIdAsync(usage.CallId!.Value))!.Value))!;
            var sent = string.Concat(detail.Content!.Request.Messages.Select(x => x.Content));
            Assert.Contains(message, sent);
            Assert.DoesNotContain("secret-account", sent);

            var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.ExceptionLogAiAnalyze && x.ActorUserId == adminId);
            Assert.True(audit.Success);
            Assert.Equal(id.ToString(), audit.TargetId);
            Assert.Matches(@"^chars=\d+; model=fake; elapsedMs=\d+; via=Api$", audit.Detail);
        }

        using (var pdf = await client.PostAsJsonAsync($"{Base}/{id}/ai-analysis/pdf", new DiagnosticAiPdfRequestDto { Markdown = done.Content!, Meta = done.Meta! }))
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        }

        using var check = factory.Services.CreateScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog
            .AnyAsync(x => x.Action == AuditActions.ExceptionLogAiAnalyzeExportPdf && x.ActorUserId == adminId && x.TargetId == id.ToString() && x.Detail!.EndsWith("via=Api")));
    }

    /// <summary>關掉視窗＝中斷請求：伺服器停止呼叫 AI（帳本記 Cancelled），稽核記「使用者取消」。</summary>
    [Fact]
    public async Task AiAnalysis_AbortedMidStream_ShouldStopTheAiCall_AndAuditAsCancelled()
    {
        var id = await RecordAsync(Unique(E2EFakeAi.SlowMarker));
        var (client, adminId) = await LoginAdminWithIdAsync();

        using (var cts = new CancellationTokenSource())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/ai-analysis");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            var first = JsonSerializer.Deserialize<DiagnosticAiStreamEventDto>((await reader.ReadLineAsync(cts.Token))!, JsonOptions)!;
            Assert.Equal(DiagnosticAiStreamEventDto.Delta, first.Type);
            cts.Cancel();
        }

        // 稽核在串流收尾之後才寫：等一下伺服器端跑完。
        for (var attempt = 0; ; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var audit = await db.AuditLog.AsNoTracking().SingleOrDefaultAsync(x => x.Action == AuditActions.ExceptionLogAiAnalyze && x.ActorUserId == adminId);
            if (audit is not null)
            {
                Assert.False(audit.Success);
                Assert.EndsWith("; 使用者取消; via=Api", audit.Detail);
                Assert.Equal(AiUsageOutcome.Cancelled, (await db.AiUsageLog.AsNoTracking().SingleAsync(x => x.UserId == adminId)).Outcome);
                break;
            }

            Assert.True(attempt < 50, "等不到伺服器端寫稽核。");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task DeleteAndPurge_ShouldRemoveRows_AndAudit()
    {
        var id = await RecordAsync(Unique("刪除"));
        var oldMessage = Unique("舊的");
        var oldId = await RecordAsync(oldMessage, occurredAt: new DateTime(2020, 1, 1, 9, 0, 0));
        using var client = await LoginAdminAsync();

        using (var deleted = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        using (var again = await client.DeleteAsync($"{Base}/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var purged = await client.PostAsync($"{Base}/purge", null))
        {
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False(await db.ExceptionLog.AnyAsync(x => x.Id == id || x.Id == oldId));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.ExceptionLogDelete && x.TargetId == id.ToString() && x.Detail == "type=NullReferenceException; via=Api"));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.ExceptionLogPurge && x.Detail!.StartsWith("days=90;") && x.Detail.EndsWith("via=Api")));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>經真的 <see cref="ExceptionLogService.RecordAsync"/> 寫一列（資料列＋堆疊檔），回傳編號。</summary>
    private async Task<int> RecordAsync(string message, string? account = null, DateTime? occurredAt = null)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExceptionLogService>().RecordAsync(new ExceptionLogEntry
        {
            ExceptionType = "System.NullReferenceException",
            Message = message,
            StackTrace = $"System.NullReferenceException: {message}\n   at MeetingRecord.Demo.Run()",
            Source = ExceptionSources.WebApi,
            Page = "/api/v1/demo",
            Operation = "Demo failed.",
            Account = account,
            OccurredAt = occurredAt ?? DateTime.Now,
        });

        return await scope.ServiceProvider.GetRequiredService<BackendDBContext>().ExceptionLog.AsNoTracking()
            .Where(x => x.Message == message).Select(x => x.Id).SingleAsync();
    }

    private async Task<HttpClient> LoginAdminAsync() => (await LoginAdminWithIdAsync()).Client;

    private async Task<(HttpClient Client, int UserId)> LoginAdminWithIdAsync()
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

/// <summary>沒有設定 AI 供應商時：設定標成 AI 不能用，AI 分析回 400（1.1.40）。</summary>
[Collection(nameof(ApiHostCollection))]
public sealed class ExceptionLogApiWithoutAiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "exception-log-no-ai-pass";

    [Fact]
    public async Task Meta_ShouldMarkAiUnavailable_AndAnalysisShouldBeABadRequest()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, []);
        int id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.MyUser.Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, true));
            var message = $"no-ai-{Guid.NewGuid():N}";
            await scope.ServiceProvider.GetRequiredService<ExceptionLogService>().RecordAsync(new ExceptionLogEntry
            {
                ExceptionType = "System.InvalidOperationException",
                Message = message,
                StackTrace = "at Demo",
                Source = ExceptionSources.Ui,
            });
            id = await db.ExceptionLog.AsNoTracking().Where(x => x.Message == message).Select(x => x.Id).SingleAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        using (var response = await client.GetAsync("/api/v1/exception-logs/meta"))
        {
            var meta = JsonSerializer.Deserialize<ApiResult<ExceptionLogMetaDto>>(await response.Content.ReadAsStringAsync(), json)!.Data!;
            Assert.False(meta.AiAvailable);
            Assert.NotEmpty(meta.AiUnavailableReason);
        }

        using var analysis = await client.PostAsync($"/api/v1/exception-logs/{id}/ai-analysis", null);
        Assert.Equal(HttpStatusCode.BadRequest, analysis.StatusCode);
        Assert.StartsWith("AI 分析目前無法使用：", JsonSerializer.Deserialize<ApiResult<object>>(await analysis.Content.ReadAsStringAsync(), json)!.ErrorMessage);
    }
}
