using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.73 轉錄引擎選項 API：<c>GET api/v1/Meeting/transcription-options</c> 的權限，
/// 以及上傳、重新轉錄選了不能用的引擎時當場回 400（測試主機沒有設定 Azure LLM Speech）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class TranscriptionOptionsApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "transcription-options-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly TranscriptionOptionsDto LlmSpeech = new() { Engine = 1, Diarize = true, MaxSpeakers = 4 };

    [Fact]
    public async Task Options_Anonymous_ShouldBeUnauthorized()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/api/v1/Meeting/transcription-options");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Options_ViewOnly_ShouldBeForbidden()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        using var client = await LoginAsync(account);

        using var response = await client.GetAsync("/api/v1/Meeting/transcription-options");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Options_Editor_ShouldReportLlmSpeechUnavailable()
    {
        var (account, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        using var client = await LoginAsync(account);

        var result = await client.GetFromJsonAsync<ApiResult<TranscriptionOptionsMetaDto>>("/api/v1/Meeting/transcription-options", JsonOptions);

        Assert.True(result!.Success);
        Assert.False(result.Data!.LlmSpeechAvailable);
        Assert.Equal(0, result.Data.DefaultEngine);
        Assert.Equal(35, result.Data.MaxSpeakers);
    }

    [Fact]
    public async Task Upload_WithUnconfiguredLlmSpeech_ShouldBeRejectedBeforeUploading()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        using var response = await client.PostAsJsonAsync("/api/v1/uploads", new UploadSessionRequestDto
        {
            Purpose = UploadPurposes.MeetingMedia,
            MeetingId = meetingId,
            FileName = "a.mp3",
            ContentType = "audio/mpeg",
            Size = 10,
            Transcription = LlmSpeech,
        });

        await AssertRejectedAsync(response, "系統尚未設定 Azure LLM Speech");
    }

    [Fact]
    public async Task Retranscribe_WithUnconfiguredLlmSpeech_ShouldBeRejected()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        using var response = await client.PostAsJsonAsync($"/api/v1/Meeting/{meetingId}/retranscribe", LlmSpeech);

        await AssertRejectedAsync(response, "系統尚未設定 Azure LLM Speech");
    }

    private async Task<int> AddMeetingAsync(int createdByUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var meeting = new Meeting { Title = $"options-{Guid.NewGuid():N}", CreatedByUserId = createdByUserId };
        db.Meeting.Add(meeting);
        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string messageStart)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = JsonSerializer.Deserialize<ApiResult<object>>(await response.Content.ReadAsStringAsync(), JsonOptions)!;
        Assert.StartsWith(messageStart, result.ErrorMessage);
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
