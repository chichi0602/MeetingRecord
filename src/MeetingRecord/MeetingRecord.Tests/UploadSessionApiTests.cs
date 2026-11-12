using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Controllers;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Tests;

/// <summary>
/// 上傳工作階段 API（<c>api/v1/uploads</c>，1.1.16）。建立前先跑服務層的上傳檢查；
/// 讀檔存檔在伺服器背景跑，而且是以上傳者的身分（資料範圍與稽核操作者都對）；取消就是右下角面板的「取消上傳」。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class UploadSessionApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "upload-session-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_WithoutCreateOrEdit_ShouldBeForbidden()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        using var response = await client.PostAsJsonAsync("/api/v1/uploads", Request(meetingId, "a.mp3", 10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldRunTheServiceChecksFirst()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        var (_, otherUserId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        var mine = await AddMeetingAsync(userId);
        var others = await AddMeetingAsync(otherUserId);
        using var client = await LoginAsync(account);

        await AssertRejectedAsync(await client.PostAsJsonAsync("/api/v1/uploads", Request(mine, "a.exe", 10)), "不支援的檔案格式");
        await AssertRejectedAsync(await client.PostAsJsonAsync("/api/v1/uploads", Request(others, "a.mp3", 10)), "沒有權限對這筆會議紀錄上傳影音檔。");
        await AssertRejectedAsync(await client.PostAsJsonAsync("/api/v1/uploads", Request(int.MaxValue, "a.mp3", 10)), "找不到要上傳影音檔的會議紀錄。");
    }

    [Fact]
    public async Task Upload_ShouldSaveMediaInTheBackground_AsTheUploader()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        var session = await CreateAsync(client, Request(meetingId, "錄音.mp3", 1000));
        Assert.Equal(1, session.ChunkCount);

        // 別人查不到。
        var (otherAccount, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using (var other = await LoginAsync(otherAccount))
        using (var notFound = await other.GetAsync($"/api/v1/uploads/{session.UploadId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        using (var chunk = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/uploads/{session.UploadId}/chunks/0") { Content = new ByteArrayContent(new byte[1000]) })
        {
            chunk.Headers.Add(UploadChunkController.CsrfHeaderName, "1");
            using var response = await client.SendAsync(chunk);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var status = await WaitUntilEndedAsync(client, session.UploadId);
        Assert.Equal(UploadStates.Succeeded, status.State);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var meeting = await db.Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId);
        Assert.Equal("錄音.mp3", meeting.MediaOriginalFileName);
        Assert.NotNull(meeting.MediaRelativePath);

        // 背景沒有請求：操作者要是上傳者本人（資料範圍也是用他的，否則上面那道「沒有權限」就擋下了）。
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.MeetingMediaUpload && x.TargetId == meetingId.ToString());
        Assert.Equal(userId, audit.ActorUserId);
    }

    /// <summary>
    /// 取消可能早在服務層開始收檔之前（WASM 頁建好工作階段就能按），也可能在收檔途中；兩種都要把「新增時附檔」的會議一起刪。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_NewMeeting_ShouldDeleteTheMeeting(bool afterUploadStarted)
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        var request = Request(meetingId, "a.mp3", 3000);
        request.DeleteMeetingOnCancel = true;
        var session = await CreateAsync(client, request);
        if (afterUploadStarted)
        {
            var notifier = factory.Services.GetRequiredService<ITranscriptionProgressNotifier>();
            for (var i = 0; i < 100 && notifier.Find(meetingId)?.Phase != TranscriptionPhase.Uploading; i++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(TranscriptionPhase.Uploading, notifier.Find(meetingId)?.Phase);
        }

        using (var cancel = await client.DeleteAsync($"/api/v1/uploads/{session.UploadId}"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var status = await WaitUntilEndedAsync(client, session.UploadId);
        Assert.Equal(UploadStates.Cancelled, status.State);
        Assert.True(status.MeetingDeleted);

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Meeting.AnyAsync(x => x.Id == meetingId));
    }

    /// <summary>
    /// 早在服務層開始收檔之前就取消（穩定重現上面那個競態的前半）：服務層還沒開始上傳、不會刪會議，要由背景工作補刪。
    /// </summary>
    [Fact]
    public async Task Cancel_BeforeTheBackgroundJobRuns_ShouldStillDeleteTheNewMeeting()
    {
        var (_, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create)]);
        var meetingId = await AddMeetingAsync(userId);
        var request = Request(meetingId, "a.mp3", 3000);
        request.DeleteMeetingOnCancel = true;

        var jobs = factory.Services.GetRequiredService<MeetingMediaUploadJobs>();
        var job = jobs.Start(userId, request, beforeRun: j => j.Cancellation.Cancel());
        for (var i = 0; i < 100 && job.State == UploadStates.Uploading; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(UploadStates.Cancelled, job.State);
        Assert.True(job.MeetingDeleted);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Meeting.AnyAsync(x => x.Id == meetingId));
    }

    /// <summary>
    /// 瀏覽器送不下去（1.1.23）：回報後立刻結束成「失敗」、帶出原因，會議保留（就算是新增時附的檔也不刪）；
    /// 別人回報不到。
    /// </summary>
    [Fact]
    public async Task Fail_ShouldEndTheUploadAsFailed_AndKeepTheMeeting()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create)]);
        var meetingId = await AddMeetingAsync(userId);
        using var client = await LoginAsync(account);

        var request = Request(meetingId, "a.mp3", 3000);
        request.DeleteMeetingOnCancel = true;
        var session = await CreateAsync(client, request);

        var (otherAccount, _) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using (var other = await LoginAsync(otherAccount))
        using (var notFound = await other.PostAsJsonAsync($"/api/v1/uploads/{session.UploadId}/fail", new UploadFailRequestDto { Message = "x" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        using (var fail = await client.PostAsJsonAsync($"/api/v1/uploads/{session.UploadId}/fail", new UploadFailRequestDto { Message = "網路不穩，重試多次仍無法上傳，請稍後重新上傳。" }))
        {
            Assert.Equal(HttpStatusCode.OK, fail.StatusCode);
        }

        var status = await WaitUntilEndedAsync(client, session.UploadId);
        Assert.Equal(UploadStates.Failed, status.State);
        Assert.Contains("網路不穩", status.Message);
        Assert.False(status.MeetingDeleted);

        using var scope = factory.Services.CreateScope();
        var meeting = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Meeting.AsNoTracking().SingleAsync(x => x.Id == meetingId);
        Assert.Null(meeting.MediaRelativePath);
    }

    private static UploadSessionRequestDto Request(int meetingId, string fileName, long size)
        => new() { Purpose = UploadPurposes.MeetingMedia, MeetingId = meetingId, FileName = fileName, ContentType = "audio/mpeg", Size = size };

    private async Task<int> AddMeetingAsync(int createdByUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var meeting = new Meeting { Title = $"upload-{Guid.NewGuid():N}", CreatedByUserId = createdByUserId };
        db.Meeting.Add(meeting);
        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private static async Task<UploadSessionDto> CreateAsync(HttpClient client, UploadSessionRequestDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/uploads", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<ApiResult<UploadSessionDto>>(body, JsonOptions)!.Data!;
    }

    private static async Task<UploadStatusDto> WaitUntilEndedAsync(HttpClient client, Guid uploadId)
    {
        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync($"/api/v1/uploads/{uploadId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = JsonSerializer.Deserialize<ApiResult<UploadStatusDto>>(await response.Content.ReadAsStringAsync(), JsonOptions)!.Data!;
            if (status.State != UploadStates.Uploading)
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("上傳一直沒有結束。");
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string messageStart)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var result = JsonSerializer.Deserialize<ApiResult<object>>(await response.Content.ReadAsStringAsync(), JsonOptions)!;
            Assert.StartsWith(messageStart, result.ErrorMessage);
        }
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
