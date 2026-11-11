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
        "/api/v1/monitoring/log-level",
        "/api/v1/monitoring/health",
        "/api/v1/monitoring/logs/recent-warnings",
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

    /// <summary>1.1.33：套用與還原日誌等級，稽核由伺服器寫（以前是畫面寫）。</summary>
    [Fact]
    public async Task LogLevel_ApplyAndRestore_ShouldChangeTheRuntimeLevel_AndAudit()
    {
        using var client = await LoginAdminAsync();
        var before = (await ReadAsync<LogLevelStateDto>(await client.GetAsync("/api/v1/monitoring/log-level"))).Data!;
        Assert.True(before.IsAvailable, "測試主機要讀得到 nlog.config 的 <logger name=\"*\"> 規則");
        var target = before.EffectiveLevel == LogLevelRank.Error ? LogLevelRank.Warn : LogLevelRank.Error;

        try
        {
            using (var applied = await client.PutAsJsonAsync("/api/v1/monitoring/log-level", new LogLevelApplyRequestDto { Level = target }))
            {
                Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
                var state = (await ReadAsync<LogLevelStateDto>(applied)).Data!;
                Assert.Equal(target, state.EffectiveLevel);
                Assert.True(state.HasOverride);
            }

            using (var restored = await client.PostAsync("/api/v1/monitoring/log-level/restore", null))
            {
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
                var state = (await ReadAsync<LogLevelStateDto>(restored)).Data!;
                Assert.False(state.HasOverride);
                Assert.Equal(state.SystemDefaultLevel, state.EffectiveLevel);
            }
        }
        finally
        {
            await client.PostAsync("/api/v1/monitoring/log-level/restore", null);
        }

        using var scope = factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.LogLevelApply || x.Action == AuditActions.LogLevelRestore)
            .OrderBy(x => x.Id).ToListAsync();
        var apply = audits.Last(x => x.Action == AuditActions.LogLevelApply);
        Assert.Equal("NLog", apply.TargetType);
        Assert.Contains($"調整為 {LogLevelRankHelper.ToLevelText(target)}", apply.Detail);
        Assert.EndsWith("via=Api", apply.Detail);
        Assert.Contains(audits, x => x.Action == AuditActions.LogLevelRestore && x.Id > apply.Id);
    }

    [Theory]
    [InlineData(LogLevelRank.Any)]
    [InlineData(LogLevelRank.Unknown)]
    public async Task LogLevel_ShouldRejectNonRealLevels(LogLevelRank level)
    {
        using var client = await LoginAdminAsync();

        using var response = await client.PutAsJsonAsync("/api/v1/monitoring/log-level", new LogLevelApplyRequestDto { Level = level });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await ReadAsync<LogLevelStateDto>(await client.GetAsync("/api/v1/monitoring/log-level"))).Data!.HasOverride);
    }

    /// <summary>1.1.35：系統健康的檢查在伺服器跑，結果經 JSON 還原後各項都在（含 required 欄位）。</summary>
    [Fact]
    public async Task Health_ShouldReturnTheFullReport()
    {
        using var client = await LoginAdminAsync();

        using var response = await client.GetAsync("/api/v1/monitoring/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await ReadAsync<SystemHealthReport>(response)).Data!;
        Assert.NotEmpty(report.Items);
        Assert.InRange(report.Score, 0, 100);
        Assert.Equal(100, report.Items.Sum(x => x.Weight));
        Assert.All(report.Items, x => Assert.False(string.IsNullOrWhiteSpace(x.Name)));
        Assert.Contains(report.Items, x => x.Group == SystemHealthGroups.Features);
    }

    [Fact]
    public async Task RecentWarnings_ShouldAnswerEvenWithoutLogFiles()
    {
        using var client = await LoginAdminAsync();

        using var response = await client.GetAsync("/api/v1/monitoring/logs/recent-warnings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await ReadAsync<LogQueryResult>(response)).Data!;
        Assert.True(result.Entries.Count <= MeetingRecord.Web.Controllers.MonitoringController.RecentWarningTake);
        Assert.All(result.Entries, x => Assert.True(x.Rank >= LogLevelRank.Warn));
    }

    /// <summary>
    /// 1.1.35：測試 AI 連線只限管理者，稽核由伺服器寫。這個測試主機沒有設定 AI，所以兩個探測都回「未設定」、不會真的花錢。
    /// </summary>
    [Fact]
    public async Task AiConnectionTest_ShouldBeAdminOnly_AndAuditedByTheServer()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using (var normal = await LoginAsync(account))
        using (var denied = await normal.PostAsync("/api/v1/monitoring/ai-connection-test", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var client = await LoginAdminAsync();
        using var response = await client.PostAsync("/api/v1/monitoring/ai-connection-test", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await ReadAsync<List<AiConnectionProbeResult>>(response)).Data!;
        Assert.Equal(2, results.Count);
        Assert.All(results, x => Assert.False(x.Success));

        using var scope = factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.AiConnectionTest).OrderByDescending(x => x.Id).FirstAsync();
        Assert.False(audit.Success);
        Assert.Equal("LlmSettings", audit.TargetType);
        Assert.EndsWith("via=Api", audit.Detail);
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
