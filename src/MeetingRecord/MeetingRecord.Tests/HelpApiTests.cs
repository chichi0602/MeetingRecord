using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Helps;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 使用說明 API（1.1.29）。說明內容與 AI 助理（串流）都要使用說明頁的檢視權限；
/// 要不要送「管理員」章節由伺服器依呼叫者判斷；稽核由伺服器寫（加 via=Api）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class HelpApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "help-api-pass";
    private const string AskUrl = "/api/v1/help/assistant/ask";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Manual_ShouldReturnTheBodyWithCategoryMarkers_WithoutTheDocumentHeader()
    {
        var (account, _) = await CreateUserAsync([MagicObjectHelper.角色_使用說明]);
        using var client = await LoginAsync(account);

        using var response = await client.GetAsync("/api/v1/help/manual");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var manual = (await ReadAsync<HelpManualDto>(response)).Data!;
        Assert.DoesNotContain("文件版本：", manual.Markdown);
        Assert.Contains("<!-- 說明分類：", manual.Markdown);
        Assert.True(manual.AssistantAvailable);
        Assert.Equal(string.Empty, manual.AssistantUnavailableReason);
    }

    [Fact]
    public async Task Ask_ShouldStreamDeltasThenDone_AndRecordUsageAndAudit()
    {
        var (account, userId) = await CreateUserAsync([MagicObjectHelper.角色_使用說明]);
        using var client = await LoginAsync(account);
        var threadKey = $"help:{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "怎麼把會議歸屬到專案？", ThreadKey = threadKey });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var events = await ReadEventsAsync(response);
        Assert.Contains(events, x => x.Type == HelpAssistantStreamEventDto.Delta);
        var done = events[^1];
        Assert.Equal(HelpAssistantStreamEventDto.Done, done.Type);
        Assert.Equal(string.Concat(events.Where(x => x.Type == HelpAssistantStreamEventDto.Delta).Select(x => x.Text)), done.Answer);
        Assert.Equal("fake", done.Model);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var log = await db.AiUsageLog.AsNoTracking().Where(x => x.UserId == userId).SingleAsync();
        Assert.Equal(AiUsageFeature.HelpAssistant, log.Feature);
        Assert.Equal(AiUsageOutcome.Succeeded, log.Outcome);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.HelpAiAsk && x.ActorUserId == userId);
        Assert.True(audit.Success);
        Assert.Equal("questionChars=11; includeAdmin=False; model=fake; outcome=成功; via=Api", audit.Detail);

        // 一般使用者：送給 AI 的說明不含「管理員」那一類；對話串編號照前端給的。
        var sent = await SentPromptAsync(scope, log.CallId);
        Assert.Contains("# 分類：新手入門", sent.Prompt);
        Assert.DoesNotContain($"# 分類：{AdminCategory}", sent.Prompt);
        Assert.Equal(threadKey, sent.ThreadKey);
    }

    /// <summary>「管理員」章節只送給管理者的 AI 上下文；由伺服器判斷，請求裡沒有可以偷改的欄位。</summary>
    [Fact]
    public async Task Ask_ShouldIncludeTheAdminChapterOnlyForAdmins()
    {
        var (account, userId) = await CreateUserAsync([MagicObjectHelper.角色_使用說明]);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackendDBContext>().MyUser
                .Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, true));
        }

        using var client = await LoginAsync(account);
        using (var response = await client.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "怎麼設定 AI 用量上限？" }))
        {
            Assert.Equal(HelpAssistantStreamEventDto.Done, (await ReadEventsAsync(response))[^1].Type);
        }

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<BackendDBContext>();
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.HelpAiAsk && x.ActorUserId == userId);
        Assert.Contains("includeAdmin=True", audit.Detail);
        var callId = await db.AiUsageLog.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.CallId).SingleAsync();
        var sent = await SentPromptAsync(check, callId);
        Assert.Contains($"# 分類：{AdminCategory}", sent.Prompt);
        Assert.StartsWith("help:", sent.ThreadKey);
    }

    private const string AdminCategory = "管理員";

    /// <summary>從 AI 對話紀錄讀出這次送給 AI 的內容（system＋user）與對話串編號。</summary>
    private static async Task<(string Prompt, string? ThreadKey)> SentPromptAsync(IServiceScope scope, Guid? callId)
    {
        var service = scope.ServiceProvider.GetRequiredService<MeetingRecord.Business.Services.DataAccess.AiCallLogService>();
        var id = await service.FindIdByCallIdAsync(callId!.Value) ?? throw new InvalidOperationException("找不到 AI 對話紀錄");
        var detail = await service.GetDetailAsync(id) ?? throw new InvalidOperationException("找不到 AI 對話紀錄");
        return (string.Concat(detail.Content!.Request.Messages.Select(x => x.Content)), detail.Content.ThreadKey);
    }

    [Fact]
    public async Task Ask_ShouldRejectBlankQuestionsAndMalformedThreadKeys()
    {
        var (account, _) = await CreateUserAsync([MagicObjectHelper.角色_使用說明]);
        using var client = await LoginAsync(account);

        using (var blank = await client.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "   " }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
            Assert.Equal("請輸入問題。", (await ReadAsync<object>(blank)).ErrorMessage);
        }

        using var badKey = await client.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "問題", ThreadKey = "chat:project:1:x" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
    }

    [Fact]
    public async Task Ask_ShouldRequireLoginAndTheHelpPagePermission()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "問題" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, userId) = await CreateUserAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(account);
        using (var response = await client.PostAsJsonAsync(AskUrl, new HelpAssistantAskRequestDto { Question = "問題" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AiUsageLog.AnyAsync(x => x.UserId == userId));
    }

    /// <summary>按「停止」＝中斷請求：伺服器停止呼叫 AI（帳本記 Cancelled），稽核記「停止」。</summary>
    [Fact]
    public async Task Ask_AbortedMidStream_ShouldStopTheAiCall_AndAuditAsStopped()
    {
        var (account, userId) = await CreateUserAsync([MagicObjectHelper.角色_使用說明]);
        using var client = await LoginAsync(account);

        using (var cts = new CancellationTokenSource())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AskUrl) { Content = JsonContent.Create(new HelpAssistantAskRequestDto { Question = "停下來" }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            var first = JsonSerializer.Deserialize<HelpAssistantStreamEventDto>((await reader.ReadLineAsync(cts.Token))!, JsonOptions)!;
            Assert.Equal(HelpAssistantStreamEventDto.Delta, first.Type);
            cts.Cancel();
        }

        // 稽核在串流收尾之後才寫：等一下伺服器端跑完。
        var audit = await WaitForAsync(async db => await db.AuditLog.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Action == AuditActions.HelpAiAsk && x.ActorUserId == userId));
        Assert.EndsWith("outcome=停止; via=Api", audit.Detail);
        Assert.False(audit.Success);

        using var scope = factory.Services.CreateScope();
        var log = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AiUsageLog.AsNoTracking().SingleAsync(x => x.UserId == userId);
        Assert.Equal(AiUsageOutcome.Cancelled, log.Outcome);
    }

    private async Task<T> WaitForAsync<T>(Func<BackendDBContext, Task<T?>> query)
        where T : class
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            if (await query(scope.ServiceProvider.GetRequiredService<BackendDBContext>()) is { } found)
            {
                return found;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("等不到伺服器端寫入。");
    }

    private Task<(string Account, int UserId)> CreateUserAsync(string[] permissions)
        => TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions);

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<List<HelpAssistantStreamEventDto>> ReadEventsAsync(HttpResponseMessage response)
        => (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<HelpAssistantStreamEventDto>(line, JsonOptions)!)
            .ToList();

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}

/// <summary>沒有設定 AI 供應商時：說明照給，助理標成不能用；提問回 400（1.1.29）。</summary>
[Collection(nameof(ApiHostCollection))]
public sealed class HelpApiWithoutAiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "help-api-no-ai-pass";

    [Fact]
    public async Task Manual_ShouldMarkTheAssistantUnavailable_AndAskShouldBeABadRequest()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_使用說明]);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        using (var response = await client.GetAsync("/api/v1/help/manual"))
        {
            var manual = JsonSerializer.Deserialize<ApiResult<HelpManualDto>>(await response.Content.ReadAsStringAsync(), json)!.Data!;
            Assert.False(manual.AssistantAvailable);
            Assert.StartsWith("AI 助理目前無法使用：", manual.AssistantUnavailableReason);
        }

        using var ask = await client.PostAsJsonAsync("/api/v1/help/assistant/ask", new HelpAssistantAskRequestDto { Question = "問題" });
        Assert.Equal(HttpStatusCode.BadRequest, ask.StatusCode);
        Assert.StartsWith("AI 助理目前無法使用：", JsonSerializer.Deserialize<ApiResult<object>>(await ask.Content.ReadAsStringAsync(), json)!.ErrorMessage);
    }
}
