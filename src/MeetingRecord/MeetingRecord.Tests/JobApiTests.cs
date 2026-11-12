using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 背景工作進度 API（<c>api/v1/jobs</c>，1.1.15），右下角進度面板的資料來源：
/// 只看得到自己資料範圍內的會議的工作，取消與關閉也只動得到看得到的（看不到回 404）；取消要會議紀錄的修改權限，
/// 上傳中的取消要用「上傳」種類。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class JobApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "job-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private ITranscriptionProgressNotifier Transcription => factory.Services.GetRequiredService<ITranscriptionProgressNotifier>();

    private IMeetingDraftProgressNotifier Draft => factory.Services.GetRequiredService<IMeetingDraftProgressNotifier>();

    private IJobCancellationRegistry Registry => factory.Services.GetRequiredService<IJobCancellationRegistry>();

    [Fact]
    public async Task Anonymous_ShouldBeUnauthorized()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/v1/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Jobs_ShouldOnlyIncludeMeetingsInTheCallersScope()
    {
        var (account, mineProjectId, othersProjectId) = await CreateUserWithProjectsAsync([PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        int mine = NewMeetingId(), others = NewMeetingId(), draft = NewMeetingId();
        Transcription.StartUpload(mine, $"我的上傳-{mine}", mineProjectId, createdByUserId: null);
        Transcription.Enqueued(others, $"別人的轉錄-{others}", othersProjectId, createdByUserId: null);
        Draft.Enqueued(draft, $"我的會議紀錄-{draft}", mineProjectId, createdByUserId: null);
        try
        {
            using var client = await LoginAsync(account);
            var jobs = await GetJobsAsync(client);

            var upload = Assert.Single(jobs, x => x.MeetingId == mine);
            Assert.Equal(BackgroundJobKinds.Transcription, upload.Kind);
            Assert.True(upload.IsUploading);
            Assert.Equal("上傳中", upload.PhaseText);
            Assert.Contains(jobs, x => x.MeetingId == draft && x.Kind == BackgroundJobKinds.Draft && x.PhaseText == "排隊中");
            Assert.DoesNotContain(jobs, x => x.MeetingId == others);
        }
        finally
        {
            Transcription.Dismiss(mine);
            Transcription.Dismiss(others);
            Draft.Dismiss(draft);
        }
    }

    [Fact]
    public async Task Cancel_ShouldNeedEdit_AndUseTheUploadKindWhileUploading()
    {
        var (viewer, viewerProjectId, _) = await CreateUserWithProjectsAsync([PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        var (editor, editorProjectId, othersProjectId) = await CreateUserWithProjectsAsync([PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]);
        int viewersJob = NewMeetingId(), uploading = NewMeetingId(), drafting = NewMeetingId(), others = NewMeetingId();
        Transcription.Enqueued(viewersJob, "檢視者看得到的轉錄", viewerProjectId, createdByUserId: null);
        Transcription.StartUpload(uploading, "上傳中的會議", editorProjectId, createdByUserId: null);
        Draft.Enqueued(drafting, "排隊中的會議紀錄", editorProjectId, createdByUserId: null);
        Transcription.Enqueued(others, "看不到的轉錄", othersProjectId, createdByUserId: null);
        try
        {
            // 只有檢視權限：看得到也不能取消。
            using (var client = await LoginAsync(viewer))
            {
                using var forbidden = await client.PostAsync($"/api/v1/jobs/{viewersJob}/cancel?kind=Transcription", null);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            Assert.False(Registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, viewersJob));

            using var editorClient = await LoginAsync(editor);

            // 上傳中：要用 Upload 種類取消，不能留下「轉錄待取消」（否則上傳完入列的那趟轉錄會被默默跳過）。
            using (var ok = await editorClient.PostAsync($"/api/v1/jobs/{uploading}/cancel?kind=Transcription", null))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }

            Assert.True(Registry.TryConsumePendingCancel(BackgroundJobKind.Upload, uploading));
            Assert.False(Registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, uploading));

            using (var ok = await editorClient.PostAsync($"/api/v1/jobs/{drafting}/cancel?kind=Draft", null))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }

            Assert.True(Registry.TryConsumePendingCancel(BackgroundJobKind.MeetingDraft, drafting));

            // 看不到的工作當成不存在；種類不對 400。
            using (var notFound = await editorClient.PostAsync($"/api/v1/jobs/{others}/cancel?kind=Transcription", null))
            {
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }

            Assert.False(Registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, others));
            using (var badKind = await editorClient.PostAsync($"/api/v1/jobs/{drafting}/cancel?kind=Upload", null))
            {
                Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
            }
        }
        finally
        {
            Transcription.Dismiss(viewersJob);
            Transcription.Dismiss(uploading);
            Draft.Dismiss(drafting);
            Transcription.Dismiss(others);
        }
    }

    [Fact]
    public async Task Dismiss_ShouldOnlyTouchVisibleJobs()
    {
        var (account, mineProjectId, othersProjectId) = await CreateUserWithProjectsAsync([PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        int mine = NewMeetingId(), mineDraft = NewMeetingId(), others = NewMeetingId();
        Transcription.Enqueued(mine, "我的轉錄", mineProjectId, createdByUserId: null);
        Draft.Enqueued(mineDraft, "我的會議紀錄", mineProjectId, createdByUserId: null);
        Transcription.Enqueued(others, "別人的轉錄", othersProjectId, createdByUserId: null);
        try
        {
            using var client = await LoginAsync(account);

            using (var notFound = await client.PostAsync($"/api/v1/jobs/{others}/dismiss?kind=Transcription", null))
            {
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }

            Assert.NotNull(Transcription.Find(others));

            using (var ok = await client.PostAsync($"/api/v1/jobs/{mine}/dismiss?kind=Transcription", null))
            {
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }

            Assert.Null(Transcription.Find(mine));
            Assert.NotNull(Draft.Find(mineDraft));

            using (var all = await client.PostAsync("/api/v1/jobs/dismiss-all", null))
            {
                Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            }

            Assert.Null(Draft.Find(mineDraft));
            Assert.NotNull(Transcription.Find(others)); // 看不到的不會被「全部關閉」關掉。
        }
        finally
        {
            Transcription.Dismiss(mine);
            Draft.Dismiss(mineDraft);
            Transcription.Dismiss(others);
        }
    }

    /// <summary>通知器是整個測試主機共用的 Singleton，用不會撞到真實資料的大編號。</summary>
    private static int NewMeetingId() => Random.Shared.Next(1_000_000, int.MaxValue);

    /// <summary>建一個使用者：屬於「我的團隊」，另有一個他所在團隊主責的專案、一個別的團隊的專案。</summary>
    private async Task<(string Account, int MineProjectId, int OthersProjectId)> CreateUserWithProjectsAsync(string[] permissions)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var myTeam = new Team { Name = $"job-mine-{suffix}", IsEnabled = true };
        var otherTeam = new Team { Name = $"job-other-{suffix}", IsEnabled = true };
        db.UserTeam.Add(new UserTeam { MyUserId = userId, Team = myTeam });
        var mine = new Project { Title = $"job-mine-{suffix}", Status = "進行中", Owner = "someone" };
        mine.Teams.Add(new ProjectTeam { Team = myTeam, IsPrimary = true });
        var others = new Project { Title = $"job-other-{suffix}", Status = "進行中", Owner = "someone" };
        others.Teams.Add(new ProjectTeam { Team = otherTeam, IsPrimary = true });
        db.Project.AddRange(mine, others);
        await db.SaveChangesAsync();
        return (account, mine.Id, others.Id);
    }

    private static async Task<List<BackgroundJobDto>> GetJobsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<ApiResult<List<BackgroundJobDto>>>(await response.Content.ReadAsStringAsync(), JsonOptions)!.Data!;
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
