using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M3（1.1.20）：會議 API 改走 <c>MeetingService</c> 之後的畫面用端點與行為。
/// 規則本身（只限上傳者、封存唯讀、背景工作進行中）在服務層測；這裡驗 API 怎麼回、稽核怎麼寫。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class MeetingApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "meeting-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] AllMeetingKeys = [MagicObjectHelper.角色_會議紀錄];

    [Fact]
    public async Task List_ShouldFilterByCategory_AndFlagUploaderAndDelete()
    {
        var world = await SeedAsync();
        using var uploader = await LoginAsync(world.UploaderAccount);
        using var member = await LoginAsync(world.MemberAccount);

        var mine = Assert.Single((await ListAsync(uploader, world.Category)).Items, x => x.Id == world.ProjectMeetingId);
        Assert.True(mine.IsUploader);
        Assert.True(mine.CanDelete);
        Assert.Equal([world.Category], mine.CategoryNames);
        Assert.Null(mine.DraftContent); // 清單不帶全文

        // 同專案的成員看得到，但不是上傳者、也不是專案負責人：不能換檔、不能刪。
        var seen = Assert.Single((await ListAsync(member, world.Category)).Items, x => x.Id == world.ProjectMeetingId);
        Assert.False(seen.IsUploader);
        Assert.False(seen.CanDelete);

        // 篩別的分類就沒有它。
        Assert.DoesNotContain((await ListAsync(uploader, $"沒有這個-{Guid.NewGuid():N}")).Items, x => x.Id == world.ProjectMeetingId);

        // 單筆才帶會議紀錄全文。
        using var single = await uploader.GetAsync($"/api/Meeting/{world.ProjectMeetingId}");
        Assert.Equal("會議紀錄內容", (await ReadAsync<MeetingDto>(single)).Data!.DraftContent);
    }

    [Fact]
    public async Task Create_ShouldIgnoreProject_AndAcceptCategoryNames()
    {
        var world = await SeedAsync();
        using var uploader = await LoginAsync(world.UploaderAccount);

        using var response = await uploader.PostAsJsonAsync("/api/Meeting", new MeetingCreateUpdateDto
        {
            Id = 0, Title = "API 新會議", CategoryNames = [$" {world.Category} ", world.Category],
        });
        var created = (await ReadAsync<MeetingDto>(response)).Data!;

        Assert.Null(created.ProjectId);
        Assert.True(created.IsUploader);
        Assert.Equal([world.Category], created.CategoryNames);
        Assert.Equal(TagStringHelper.ToStored([world.Category]), created.Categories);
    }

    [Fact]
    public async Task Update_OnArchivedProject_ShouldBeRejected()
    {
        // 1.1.20 外部行為變更：以前 API 的修改不看封存，現在與畫面一樣唯讀（管理者除外）。
        var world = await SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.Project.Where(x => x.Id == world.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProjectScopeRules.ArchivedStatus));
        }

        using var uploader = await LoginAsync(world.UploaderAccount);
        using var response = await uploader.PutAsJsonAsync($"/api/Meeting/{world.ProjectMeetingId}", new MeetingCreateUpdateDto { Id = world.ProjectMeetingId, Title = "改名" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await ReadAsync<object>(response);
        Assert.Equal(ProjectArchiveRules.ArchivedMessage, result.Errors!["Validation"][0]);
    }

    [Fact]
    public async Task Transcript_ReadDownloadCopy_ShouldWorkAndAudit()
    {
        var world = await SeedAsync(withTranscript: "第一行\n第二行");
        using var uploader = await LoginAsync(world.UploaderAccount);

        using (var text = await uploader.GetAsync($"/api/v1/Meeting/{world.ProjectMeetingId}/transcript"))
        {
            Assert.Equal("第一行\n第二行", (await ReadAsync<MeetingTranscriptDto>(text)).Data!.Text);
        }

        using (var file = await uploader.GetAsync($"/api/v1/Meeting/{world.ProjectMeetingId}/transcript/file"))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            var bytes = await file.Content.ReadAsByteArrayAsync();
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // 含 BOM，記事本直接開不亂碼
            Assert.EndsWith(".txt", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName);
        }

        using (var copied = await uploader.PostAsync($"/api/v1/Meeting/{world.ProjectMeetingId}/transcript/copied", null))
        {
            Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var target = world.ProjectMeetingId.ToString();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.MeetingTranscriptDownload && x.TargetId == target));
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.MeetingTranscriptCopy && x.TargetId == target && x.Detail!.Contains("chars=7")));
    }

    [Fact]
    public async Task DraftPdf_ShouldUseUnsavedContent_AndAudit()
    {
        var world = await SeedAsync();
        string? renderedHtml = null;
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IPdfRenderer>(new FakePdfRenderer(html => renderedHtml = html))));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, world.UploaderAccount, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");

        using var response = await client.PostAsJsonAsync($"/api/v1/Meeting/{world.ProjectMeetingId}/draft/pdf", new MeetingPdfRequestDto { Content = "還沒存的新內容" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("還沒存的新內容", renderedHtml);
        using var scope = host.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog
            .AnyAsync(x => x.Action == AuditActions.MeetingExportPdf && x.TargetId == world.ProjectMeetingId.ToString()));
    }

    [Fact]
    public async Task UpdateDraft_AndAttachToProject_ShouldGoThroughTheService()
    {
        var world = await SeedAsync();
        using var uploader = await LoginAsync(world.UploaderAccount);

        using (var saved = await uploader.PutAsJsonAsync($"/api/v1/Meeting/{world.ProjectMeetingId}/draft", new MeetingDraftContentDto { Content = "改過的會議紀錄" }))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var attached = await uploader.PutAsJsonAsync($"/api/v1/Meeting/{world.UnassignedMeetingId}/project", new MeetingAttachRequestDto { ProjectId = world.ProjectId }))
        {
            Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.Equal("改過的會議紀錄", (await db.Meeting.AsNoTracking().SingleAsync(x => x.Id == world.ProjectMeetingId)).DraftContent);
        Assert.Equal(world.ProjectId, (await db.Meeting.AsNoTracking().SingleAsync(x => x.Id == world.UnassignedMeetingId)).ProjectId);
    }

    [Fact]
    public async Task ViewOnly_ShouldNotTriggerWritesOrExports()
    {
        var world = await SeedAsync(withTranscript: "逐字稿");
        var (viewer, viewerId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]);
        await JoinTeamAsync(world.TeamId, viewerId);
        using var client = await LoginAsync(viewer);
        var id = world.ProjectMeetingId;

        foreach (var response in new[]
        {
            await client.PostAsync($"/api/v1/Meeting/{id}/retranscribe", null),
            await client.PostAsJsonAsync($"/api/v1/Meeting/{id}/draft", new MeetingDraftRequestDto { PromptTemplateId = 1 }),
            await client.PutAsJsonAsync($"/api/v1/Meeting/{id}/draft", new MeetingDraftContentDto { Content = "x" }),
            await client.GetAsync($"/api/v1/Meeting/{id}/transcript/file"),
            await client.PostAsJsonAsync($"/api/v1/Meeting/{id}/draft/pdf", new MeetingPdfRequestDto()),
        })
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        // 看得到：逐字稿全文可以讀。
        using var text = await client.GetAsync($"/api/v1/Meeting/{id}/transcript");
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
    }

    private sealed record World(string UploaderAccount, string MemberAccount, int TeamId, int ProjectId, int ProjectMeetingId, int UnassignedMeetingId, string Category);

    /// <summary>上傳者（也是專案負責人以外的人）與同團隊成員；專案底下一場會議（有分類、有會議紀錄）、上傳者另有一場未歸屬的。</summary>
    private async Task<World> SeedAsync(string? withTranscript = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (uploaderAccount, uploaderId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, AllMeetingKeys);
        var (memberAccount, memberId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, AllMeetingKeys);
        var category = $"分類-{suffix}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"meeting-api-{suffix}", IsEnabled = true };
        db.UserTeam.AddRange(new UserTeam { MyUserId = uploaderId, Team = team }, new UserTeam { MyUserId = memberId, Team = team });
        var project = new Project { Title = $"meeting-api-{suffix}", Status = "進行中", Owner = "別人" };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        db.Project.Add(project);
        await db.SaveChangesAsync();

        var meeting = new Meeting
        {
            Title = $"會議-{suffix}",
            ProjectId = project.Id,
            CreatedByUserId = uploaderId,
            Categories = TagStringHelper.ToStored([category]),
            DraftContent = "會議紀錄內容",
            DraftStatus = DraftStatus.Completed,
        };
        if (withTranscript is not null)
        {
            meeting.TranscriptRelativePath = await scope.ServiceProvider.GetRequiredService<MeetingFileStore>()
                .SaveTranscriptAsync(DateTime.Now, withTranscript, CancellationToken.None);
            meeting.TranscriptionStatus = TranscriptionStatus.Completed;
        }

        var unassigned = new Meeting { Title = $"未歸屬-{suffix}", CreatedByUserId = uploaderId };
        db.Meeting.AddRange(meeting, unassigned);
        await db.SaveChangesAsync();
        return new World(uploaderAccount, memberAccount, team.Id, project.Id, meeting.Id, unassigned.Id, category);
    }

    private async Task JoinTeamAsync(int teamId, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        db.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = teamId });
        await db.SaveChangesAsync();
    }

    private static async Task<PagedResult<MeetingDto>> ListAsync(HttpClient client, string category)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Meeting/list", new MeetingListRequestDto { CategoryFilters = [category], PageIndex = 1, PageSize = 50 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<MeetingDto>>(response)).Data!;
    }

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

    private sealed class FakePdfRenderer(Action<string> onRender) : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
        {
            onRender(html);
            return Task.FromResult("%PDF-fake"u8.ToArray());
        }
    }
}
