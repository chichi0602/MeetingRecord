using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Testing;

namespace MeetingRecord.Tests;

/// <summary>換上假 AI（端對端用的那一套）與假 PDF 的測試主機，AI 問答 API 用。1.1.42 起可繼承（日誌檢視 API 再換上假日誌）。</summary>
public class AiChatTestApplicationFactory : ApiTestApplicationFactory
{
    protected override Dictionary<string, string?> CreateSettings()
    {
        var settings = base.CreateSettings();
        settings["LlmSettings:DefaultProvider"] = E2EFakeAi.ProviderName;
        settings["LlmSettings:TranscriptionProvider"] = E2EFakeAi.ProviderName;
        settings["LlmSettings:Providers:Fake:ApiKey"] = "api-test-fake-key";
        settings["LlmSettings:Providers:Fake:Endpoint"] = "https://fake.invalid/";
        settings["LlmSettings:Providers:Fake:Model"] = "fake";
        settings["LlmSettings:Providers:Fake:TranscriptionModel"] = "fake";
        return settings;
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddE2EFakeAi();
            services.AddSingleton<IPdfRenderer, FakePdf>();
        });
    }

    private sealed class FakePdf : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) => Task.FromResult("%PDF-fake"u8.ToArray());
    }
}

/// <summary>
/// WASM 遷移 M3（1.1.21）：AI 問答 API。提問與重新產生是串流（一行一筆 JSON）；第一段文字前就失敗回一般的 404／400。
/// 動作權限依範圍：專案看專案項目，會議看會議紀錄或專案項目；資料範圍由服務層檢查。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class AiChatApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "ai-chat-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Ask_ShouldStreamDeltas_ThenDone_AndSaveTheConversation()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/ai-chat/meeting/{world.MeetingId}";
        var conversationId = await CreateConversationAsync(client, baseUrl);

        using var response = await AskAsync(client, $"{baseUrl}/conversations/{conversationId}/ask", "這場會議的結論是什麼？");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var events = await ReadEventsAsync(response);
        Assert.Contains(events, x => x.Type == AiChatStreamEventDto.Delta);
        var done = events[^1];
        Assert.Equal(AiChatStreamEventDto.Done, done.Type);
        Assert.Equal(string.Concat(events.Where(x => x.Type == AiChatStreamEventDto.Delta).Select(x => x.Text)), done.Answer!.Answer);

        using var history = await client.GetAsync($"{baseUrl}/conversations/{conversationId}/messages");
        var messages = (await ReadAsync<List<AiChatMessageDto>>(history)).Data!;
        Assert.Equal(["user", "assistant"], messages.Select(x => x.Role));
        Assert.Equal("這場會議的結論是什麼？", messages[0].Content);
    }

    [Fact]
    public async Task Ask_FailingBeforeTheFirstDelta_ShouldBeAPlainError()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/ai-chat/meeting/{world.MeetingId}";
        var conversationId = await CreateConversationAsync(client, baseUrl);

        // 附件太多：服務層在產生之前就擋下 → 一般的 400（不是 200 加 error 行）。
        var files = Enumerable.Range(1, 6).Select(i => ($"說明{i}.txt", "內容"u8.ToArray())).ToArray();
        using (var tooMany = await AskAsync(client, $"{baseUrl}/conversations/{conversationId}/ask", "問題", files))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Equal("一次最多附上 5 個檔案。", (await ReadAsync<object>(tooMany)).ErrorMessage);
        }

        // 看不到的會議：當成不存在。
        using var hidden = await AskAsync(client, $"/api/v1/ai-chat/meeting/{world.HiddenMeetingId}/conversations/x/ask", "問題");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Permissions_ShouldFollowTheScope()
    {
        // 只有專案項目的檢視：專案與「專案底下的會議」的問答都看得到（專案頁的入口）。
        var projectViewer = await SeedAsync([PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View)]);
        using (var client = await LoginAsync(projectViewer.Account))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/ai-chat/project/{projectViewer.ProjectId}/conversations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/ai-chat/meeting/{projectViewer.MeetingId}/conversations")).StatusCode);

            // 重新命名要修改權限。
            var conversationId = await CreateConversationAsync(client, $"/api/v1/ai-chat/project/{projectViewer.ProjectId}");
            using var rename = await client.PutAsJsonAsync($"/api/v1/ai-chat/project/{projectViewer.ProjectId}/conversations/{conversationId}/title", new AiChatRenameRequestDto { Title = "新名字" });
            Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        }

        // 只有會議紀錄的權限：會議的問答可以，專案的不行。
        var meetingUser = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using (var client = await LoginAsync(meetingUser.Account))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/ai-chat/meeting/{meetingUser.MeetingId}/conversations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/ai-chat/project/{meetingUser.ProjectId}/conversations")).StatusCode);
        }
    }

    [Fact]
    public async Task Attachment_Export_AndConflict_ShouldWork()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/ai-chat/meeting/{world.MeetingId}";
        var conversationId = await CreateConversationAsync(client, baseUrl);
        using (var asked = await AskAsync(client, $"{baseUrl}/conversations/{conversationId}/ask", "看附件", ("規格.txt", Encoding.UTF8.GetBytes("規格內容"))))
        {
            Assert.Equal(AiChatStreamEventDto.Done, (await ReadEventsAsync(asked))[^1].Type);
        }

        using var history = await client.GetAsync($"{baseUrl}/conversations/{conversationId}/messages");
        var messages = (await ReadAsync<List<AiChatMessageDto>>(history)).Data!;
        var attachment = Assert.Single(messages[0].Attachments);

        // 下載文件寫稽核；不在這段對話裡的檔名 404。
        using (var file = await client.GetAsync($"{baseUrl}/conversations/{conversationId}/attachments/{attachment.StoredName}?download=true"))
        {
            Assert.Equal("規格內容", Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync()));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{baseUrl}/conversations/{conversationId}/attachments/not-there.txt")).StatusCode);

        // 匯出整段對話；超出範圍的訊息 404。
        using (var pdf = await client.PostAsJsonAsync($"{baseUrl}/conversations/{conversationId}/pdf", new AiChatExportRequestDto()))
        {
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{baseUrl}/conversations/{conversationId}/pdf", new AiChatExportRequestDto { MessageIndex = 9 })).StatusCode);

        // 修改訊息時原內容對不上（別人先改過）回 409。
        using var conflict = await client.PutAsJsonAsync($"{baseUrl}/conversations/{conversationId}/messages/1", new AiChatUpdateMessageRequestDto
        {
            Role = "assistant", Content = "不是原本的內容", NewContent = "新內容",
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var target = world.MeetingId.ToString();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.AiChatAttachmentDownload && x.TargetId == target));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.AiChatExportPdf && x.TargetId == target));
    }

    [Fact]
    public async Task CreateConversation_Automatic_ShouldNotBeAudited()
    {
        // 1.1.22：開視窗時自動建的第一段對話不算使用者的動作（同 Server 頁），使用者按「開新對話」才寫稽核。
        var world = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/ai-chat/meeting/{world.MeetingId}";

        string automatic, manual;
        using (var response = await client.PostAsync($"{baseUrl}/conversations?automatic=true", null))
        {
            automatic = (await ReadAsync<string>(response)).Data!;
        }

        using (var response = await client.PostAsync($"{baseUrl}/conversations", null))
        {
            manual = (await ReadAsync<string>(response)).Data!;
        }

        using var scope = factory.Services.CreateScope();
        var details = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
            .Where(x => x.Action == AuditActions.AiChatCreate).Select(x => x.Detail).ToListAsync();
        Assert.DoesNotContain(details, x => x!.Contains(automatic));
        Assert.Contains(details, x => x!.Contains(manual));
    }

    [Fact]
    public async Task Ask_ShouldAttributeUsageToTheCaller()
    {
        // 1.1.28：經 API 提問時，用量帳本要記到呼叫者（每人額度靠它）。以前服務層只看 CurrentUserService，API 呼叫時是空的。
        var world = await SeedAsync([MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/ai-chat/meeting/{world.MeetingId}";
        var conversationId = await CreateConversationAsync(client, baseUrl);
        using (var response = await AskAsync(client, $"{baseUrl}/conversations/{conversationId}/ask", "誰問的？"))
        {
            Assert.Equal(AiChatStreamEventDto.Done, (await ReadEventsAsync(response))[^1].Type);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var userId = await db.MyUser.Where(x => x.Account == world.Account).Select(x => x.Id).SingleAsync();
        var log = await db.AiUsageLog.AsNoTracking().Where(x => x.MeetingId == world.MeetingId).OrderByDescending(x => x.Id).FirstAsync();
        Assert.Equal(userId, log.UserId);
        Assert.False(string.IsNullOrWhiteSpace(log.UserName));
    }

    private sealed record World(string Account, int ProjectId, int MeetingId, int HiddenMeetingId);

    /// <summary>使用者所在團隊主責的專案、專案底下一場有會議紀錄的會議；另一場別人上傳、未歸屬的會議（看不到）。</summary>
    private async Task<World> SeedAsync(string[] permissions)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"ai-chat-{suffix}", IsEnabled = true };
        db.UserTeam.Add(new UserTeam { MyUserId = userId, Team = team });
        var project = new Project { Title = $"ai-chat-{suffix}", Status = "進行中", Owner = "負責人" };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        db.Project.Add(project);
        await db.SaveChangesAsync();
        var meeting = new Meeting { Title = $"會議-{suffix}", ProjectId = project.Id, CreatedByUserId = userId, DraftContent = "決議：下週上線。", DraftStatus = MeetingRecord.Share.Enums.DraftStatus.Completed };
        var hidden = new Meeting { Title = $"別人的-{suffix}" };
        db.Meeting.AddRange(meeting, hidden);
        await db.SaveChangesAsync();
        return new World(account, project.Id, meeting.Id, hidden.Id);
    }

    private static async Task<string> CreateConversationAsync(HttpClient client, string baseUrl)
    {
        using var response = await client.PostAsync($"{baseUrl}/conversations", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<string>(response)).Data!;
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, string url, string question, params (string Name, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(question), "question" } };
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(part, "files", name);
        }

        return client.PostAsync(url, form);
    }

    private static async Task<List<AiChatStreamEventDto>> ReadEventsAsync(HttpResponseMessage response)
        => (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<AiChatStreamEventDto>(line, JsonOptions)!)
            .ToList();

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
