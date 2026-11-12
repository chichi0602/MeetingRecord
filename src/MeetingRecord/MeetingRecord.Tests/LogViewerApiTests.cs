using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Testing;

namespace MeetingRecord.Tests;

/// <summary>假 AI、假 PDF 再加上假日誌的測試主機（1.1.42）：測試主機沒有 NLog 檔案，日誌由這裡給。</summary>
public sealed class LogViewerTestApplicationFactory : AiChatTestApplicationFactory
{
    /// <summary>關鍵字是這個時回空結果。</summary>
    public const string NoMatchKeyword = "沒有這種日誌";

    public const string Marker = "log-viewer-api-marker";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<ILogQueryService, FakeLogs>());
    }

    private sealed class FakeLogs : ILogQueryService
    {
        public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken cancellationToken = default)
        {
            var entries = request.Keyword == NoMatchKeyword
                ? []
                : Enumerable.Range(1, 3).Select(i => new LogEntry
                {
                    Sequence = i,
                    Timestamp = request.StartTime.AddMinutes(i),
                    Level = "ERROR",
                    Rank = LogLevelRank.Error,
                    Message = $"{Marker} #{i}",
                    Raw = $"2026-11-11 10:0{i}:00|ERROR|{Marker} #{i}",
                }).ToList();

            return Task.FromResult(new LogQueryResult
            {
                Entries = entries,
                AppliedStartTime = request.StartTime,
                AppliedEndTime = request.EndTime,
            });
        }
    }
}

/// <summary>
/// 日誌檢視 API（<c>api/v1/monitoring/logs/…</c>，1.1.42），只限管理者。
/// 匯出 .log 與 AI 日誌分析都由伺服器依同一組條件重查（不收前端給的日誌），稽核由伺服器寫（via=Api）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class LogViewerApiTests(LogViewerTestApplicationFactory factory) : IClassFixture<LogViewerTestApplicationFactory>
{
    private const string Password = "log-viewer-api-pass";
    private const string Base = "/api/v1/monitoring/logs";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Start = new(2026, 11, 11, 10, 0, 0);
    private static readonly DateTime End = new(2026, 11, 11, 11, 0, 0);

    [Fact]
    public async Task NonAdmins_ShouldBeRejected_WithoutCallingTheAi()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        foreach (var request in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => client.GetAsync($"{Base}/meta"),
                     () => client.PostAsJsonAsync($"{Base}/query", Query()),
                     () => client.PostAsJsonAsync($"{Base}/export", Query()),
                     () => client.PostAsJsonAsync($"{Base}/ai-analysis", Query()),
                 })
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AiUsageLog.AnyAsync(x => x.UserId == userId));
    }

    [Fact]
    public async Task QueryAndExport_ShouldReturnTheLogs_AndAuditTheExport()
    {
        var (client, adminId) = await LoginAdminAsync();

        var meta = (await ReadAsync<LogViewerMetaDto>(await client.GetAsync($"{Base}/meta"))).Data!;
        Assert.True(meta.AiAvailable);

        var result = (await ReadAsync<LogQueryResult>(await client.PostAsJsonAsync($"{Base}/query", Query()))).Data!;
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(Start, result.AppliedStartTime);

        using (var export = await client.PostAsJsonAsync($"{Base}/export", Query()))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            var bytes = await export.Content.ReadAsByteArrayAsync();
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            Assert.Equal(string.Join(Environment.NewLine, result.Entries.Select(x => x.Raw)), Encoding.UTF8.GetString(bytes[3..]));
        }

        using (var empty = await client.PostAsJsonAsync($"{Base}/export", Query(LogViewerTestApplicationFactory.NoMatchKeyword)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.LogViewerExport && x.ActorUserId == adminId);
        Assert.Equal("LogQuery", audit.TargetType);
        Assert.Equal("20261111100000-20261111110000", audit.TargetId);
        Assert.Equal("format=log; rows=3; via=Api", audit.Detail);
    }

    [Fact]
    public async Task AiAnalysis_ShouldStreamFromAServerSideQuery_AndAudit()
    {
        var (client, adminId) = await LoginAdminAsync();

        using var response = await client.PostAsJsonAsync($"{Base}/ai-analysis", Query());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<DiagnosticAiStreamEventDto>(line, JsonOptions)!)
            .ToList();
        Assert.Contains(events, x => x.Type == DiagnosticAiStreamEventDto.Delta);
        var done = events[^1];
        Assert.Equal(DiagnosticAiStreamEventDto.Done, done.Type);
        Assert.Contains(E2EFakeAi.AnswerMarker, done.Content);
        Assert.Contains(done.Meta!, x => x.Label == "最低等級" && x.Value == "WARN");
        Assert.Contains(done.Meta!, x => x.Label == "分析範圍" && x.Value == "分析 3 筆／查詢 3 筆");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var usage = await db.AiUsageLog.AsNoTracking().SingleAsync(x => x.UserId == adminId);
            Assert.Equal(AiUsageFeature.LogAnalysis, usage.Feature);

            // 送出的是伺服器自己查到的日誌（請求裡只有條件）。
            var callLogs = scope.ServiceProvider.GetRequiredService<AiCallLogService>();
            var detail = (await callLogs.GetDetailAsync((await callLogs.FindIdByCallIdAsync(usage.CallId!.Value))!.Value))!;
            Assert.Contains($"{LogViewerTestApplicationFactory.Marker} #3", string.Concat(detail.Content!.Request.Messages.Select(x => x.Content)));

            var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.LogViewerAiAnalyze && x.ActorUserId == adminId);
            Assert.True(audit.Success);
            Assert.Equal("20261111100000-20261111110000", audit.TargetId);
            Assert.Matches(@"^送出 3/3 筆、\d+ 字元；模型 fake；耗時 \d+ ms; via=Api$", audit.Detail);
        }

        using (var pdf = await client.PostAsJsonAsync($"{Base}/ai-analysis/pdf", new LogAiPdfRequestDto { Query = Query(), Markdown = done.Content!, Meta = done.Meta! }))
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        }

        using (var empty = await client.PostAsJsonAsync($"{Base}/ai-analysis", Query(LogViewerTestApplicationFactory.NoMatchKeyword)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        using var check = factory.Services.CreateScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog
            .AnyAsync(x => x.Action == AuditActions.LogViewerAiAnalyzeExportPdf && x.ActorUserId == adminId && x.Detail!.StartsWith("rows=3; bytes=") && x.Detail.EndsWith("via=Api")));
    }

    private static LogQueryRequest Query(string keyword = "") => new()
    {
        StartTime = Start,
        EndTime = End,
        MinimumLevel = LogLevelRank.Warn,
        Keyword = keyword,
    };

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
