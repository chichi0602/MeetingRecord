using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Controllers;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Tests;

/// <summary>
/// 分塊上傳端點 <c>PUT /api/v1/uploads/{id}/chunks/{index}</c>（1.0.59）。
/// 這支走網頁登入 Cookie（不是 JWT），測試用 App 自己的 Cookie 票證格式簽一張真的登入 Cookie。
/// </summary>
public sealed partial class ApiIntegrationTests
{
    private const int UploadChunk = ChunkedUploadStore.ChunkSize;

    [Fact]
    public async Task UploadChunk_WithoutLoginCookie_ShouldReturn401()
    {
        // 瀏覽器端看到 401 才知道是登入失效（不重試）；被狀態碼頁面重跑成 405 的話會被當成網路問題一直重試。
        var session = UploadStore.Create(ownerUserId: 900001, 10);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(ChunkRequest(session.Id, 0, new byte[10]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_WithJwtOnly_ShouldNotBeAccepted()
    {
        // 這支只認網頁 Cookie：外部用戶端的 JWT 不能拿來塞別人的上傳。
        var session = UploadStore.Create(ownerUserId: 900002, 10);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await AuthorizeAsync(client);

        using var response = await client.SendAsync(ChunkRequest(session.Id, 0, new byte[10]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_WithoutCsrfHeader_ShouldReturn400()
    {
        const int userId = 900003;
        var session = UploadStore.Create(userId, 10);
        using var client = CreateCookieClient(userId);

        using var request = ChunkRequest(session.Id, 0, new byte[10]);
        request.Headers.Remove(UploadChunkController.CsrfHeaderName);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_ToSomeoneElsesSession_ShouldReturn404()
    {
        var session = UploadStore.Create(ownerUserId: 900004, 10);
        using var client = CreateCookieClient(userId: 900005);

        using var response = await client.SendAsync(ChunkRequest(session.Id, 0, new byte[10]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_LargerThanOneChunk_ShouldBeRejected()
    {
        const int userId = 900006;
        var session = UploadStore.Create(userId, UploadChunk * 2L);
        using var client = CreateCookieClient(userId);

        using var response = await client.SendAsync(ChunkRequest(session.Id, 0, new byte[UploadChunk + 1]));

        Assert.False(response.IsSuccessStatusCode);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_AfterTheReaderStopped_ShouldReturn409()
    {
        // 服務層拒絕或取消之後，瀏覽器送下一塊要拿到 409 才會收手。
        const int userId = 900007;
        var session = UploadStore.Create(userId, UploadChunk * 2L);
        using var client = CreateCookieClient(userId);
        await session.Stream.DisposeAsync();

        using var response = await client.SendAsync(ChunkRequest(session.Id, 0, new byte[UploadChunk]));

        Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"實際 {(int)response.StatusCode}");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UploadChunk_HappyPath_ShouldDeliverTheBytesToTheReader()
    {
        const int userId = 900008;
        var payload = new byte[UploadChunk + 1234];
        new Random(42).NextBytes(payload);
        await using var session = UploadStore.Create(userId, payload.Length);
        using var client = CreateCookieClient(userId);

        var reading = Task.Run(async () =>
        {
            using var memory = new MemoryStream();
            await session.Stream.CopyToAsync(memory);
            return memory.ToArray();
        });

        using (var first = await client.SendAsync(ChunkRequest(session.Id, 0, payload[..UploadChunk])))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var resend = await client.SendAsync(ChunkRequest(session.Id, 0, payload[..UploadChunk])))
        {
            Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        }

        using (var last = await client.SendAsync(ChunkRequest(session.Id, 1, payload[UploadChunk..])))
        {
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }

        Assert.Equal(payload, await reading);
    }

    private ChunkedUploadStore UploadStore => factory.Services.GetRequiredService<ChunkedUploadStore>();

    private static HttpRequestMessage ChunkRequest(Guid uploadId, int index, byte[] data)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/uploads/{uploadId}/chunks/{index}")
        {
            Content = new ByteArrayContent(data),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(UploadChunkController.CsrfHeaderName, "1");
        return request;
    }

    /// <summary>用 App 自己的 Cookie 票證格式簽一張登入 Cookie（與 Login.razor.cs 放一樣的 Sid）。</summary>
    private HttpClient CreateCookieClient(int userId)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(MagicObjectHelper.CookieScheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Sid, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, $"cookie-user-{userId}"),
                new Claim(ClaimTypes.Name, $"cookie-user-{userId}"),
            ],
            MagicObjectHelper.CookieScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            MagicObjectHelper.CookieScheme);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return client;
    }
}
