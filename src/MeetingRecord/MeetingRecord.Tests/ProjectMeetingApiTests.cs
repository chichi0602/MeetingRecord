using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Testing;

namespace MeetingRecord.Tests;

/// <summary>
/// 專案頁裡的會議與待辦抽取 API（1.1.25）。用假 AI、假 PDF 的測試主機（<see cref="AiChatTestApplicationFactory"/>）。
/// 重點：只看「專案項目」權限（不借用會議紀錄頁）、會議要屬於這個專案、移出的權限與上傳者規則、稽核 <c>page=Project; via=Api</c>、
/// 待辦抽取在已完成的專案被擋（1.1.25 服務層補上）。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class ProjectMeetingApiTests(AiChatTestApplicationFactory factory) : IClassFixture<AiChatTestApplicationFactory>
{
    private const string Password = "project-meeting-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>只有專案項目權限（沒有會議紀錄頁的權限）也能看會議、改會議紀錄；別的專案的會議經這個專案的路由一律 404。</summary>
    [Fact]
    public async Task ProjectPermissionAlone_ShouldBeEnough_AndMeetingMustBelongToTheProject()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_專案項目]);
        var other = await SeedAsync([MagicObjectHelper.角色_專案項目]);
        using var client = await LoginAsync(world.Account);
        var baseUrl = $"/api/v1/Project/{world.ProjectId}";

        var meetings = await GetAsync<List<MeetingDto>>(client, $"{baseUrl}/meetings");
        var listed = Assert.Single(meetings);
        Assert.Null(listed.DraftContent);
        Assert.True(listed.IsUploader);
        Assert.Equal("決議：下週上線。", (await GetAsync<MeetingDto>(client, $"{baseUrl}/meetings/{world.MeetingId}")).DraftContent);

        using (var update = await client.PutAsJsonAsync($"{baseUrl}/meetings/{world.MeetingId}/draft", new MeetingDraftContentDto { Content = "改過的會議紀錄" }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        // 看得到、但屬於自己另一個專案的會議：經這個專案的路由也是 404（不能拿 A 專案的權限動 B 專案的會議）。
        var siblingMeetingId = await AddSiblingProjectMeetingAsync(world.ProjectId);
        using (var foreign = await client.GetAsync($"{baseUrl}/meetings/{siblingMeetingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var foreignDraft = await client.PutAsJsonAsync($"{baseUrl}/meetings/{siblingMeetingId}/draft", new MeetingDraftContentDto { Content = "不該改到" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignDraft.StatusCode);
        }

        // 可選逐字稿要帶會議紀錄狀態（1.1.26）：畫面據此提醒「重新產生將覆蓋既有會議紀錄」、決定能不能送出。
        var transcripts = await GetAsync<List<MeetingRecord.Dtos.Projects.ProjectTranscriptOptionDto>>(client, $"{baseUrl}/transcripts/selectable");
        Assert.Contains(transcripts, x => x.Id == world.MeetingId && x.HasDraft && x.DraftStatus == (int)MeetingRecord.Share.Enums.DraftStatus.Completed);

        using (var hiddenProject = await client.GetAsync($"/api/v1/Project/{other.ProjectId}/meetings"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenProject.StatusCode);
        }
    }

    /// <summary>移出：專案項目的修改或刪除任一即可；只有檢視 403；不是上傳者被服務層擋下；成功後會議不再屬於專案。</summary>
    [Fact]
    public async Task Detach_ShouldNeedEditOrDelete_AndOnlyTheUploader()
    {
        var viewer = await SeedAsync([PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View)]);
        using (var client = await LoginAsync(viewer.Account))
        using (var response = await client.DeleteAsync($"/api/v1/Project/{viewer.ProjectId}/meetings/{viewer.MeetingId}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var uploader = await SeedAsync([PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View), PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]);
        var (memberAccount, memberId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_專案項目]);
        await JoinTeamOfProjectAsync(memberId, uploader.ProjectId);
        using (var member = await LoginAsync(memberAccount))
        using (var response = await member.DeleteAsync($"/api/v1/Project/{uploader.ProjectId}/meetings/{uploader.MeetingId}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var client = await LoginAsync(uploader.Account))
        using (var response = await client.DeleteAsync($"/api/v1/Project/{uploader.ProjectId}/meetings/{uploader.MeetingId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Meeting.AsNoTracking().SingleAsync(x => x.Id == uploader.MeetingId)).ProjectId);
    }

    [Fact]
    public async Task Pdfs_ShouldBeRenderedByTheServer_AndAuditedAsProjectPage()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_專案項目]);
        using var client = await LoginAsync(world.Account);

        using (var pdf = await client.PostAsJsonAsync($"/api/v1/Project/{world.ProjectId}/meetings/{world.MeetingId}/draft/pdf", new MeetingPdfRequestDto { Content = "還沒存的內容" }))
        {
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }

        using (var todos = await client.GetAsync($"/api/v1/Project/{world.ProjectId}/todos/pdf"))
        {
            Assert.Equal(HttpStatusCode.OK, todos.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var meetingAudit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.MeetingExportPdf && x.TargetId == world.MeetingId.ToString());
        Assert.EndsWith("page=Project; via=Api", meetingAudit.Detail);
        var todoAudit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.ProjectTodoExportPdf && x.TargetId == world.ProjectId.ToString());
        Assert.EndsWith("via=Api", todoAudit.Detail);
    }

    /// <summary>抽出待辦（假 AI）→ 加入新待辦（記下來源會議、稽核 via=AiExtract）；沒有待辦事項的新增權限 403；已完成的專案被擋（花錢的動作）。</summary>
    [Fact]
    public async Task TodoExtraction_ShouldExtractAndAdd_ButNotOnArchivedProjects()
    {
        var world = await SeedAsync([MagicObjectHelper.角色_專案項目, MagicObjectHelper.角色_待辦事項]);
        using var client = await LoginAsync(world.Account);

        using (var extract = await client.PostAsync($"/api/v1/todo-extraction/meetings/{world.MeetingId}", null))
        {
            Assert.Equal(HttpStatusCode.OK, extract.StatusCode);
            var result = (await ReadAsync<TodoExtractionResultDto>(extract)).Data!;
            Assert.Contains(result.Items, x => x.Title == E2EFakeAi.ExtractedTodoTitle);

            // 1.1.28：用量帳本記到呼叫者（以前經 API 是空的，每人額度擋不住）。
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var userId = await db.MyUser.Where(x => x.Account == world.Account).Select(x => x.Id).SingleAsync();
            Assert.Equal(userId, (await db.AiUsageLog.AsNoTracking().Where(x => x.MeetingId == world.MeetingId).OrderByDescending(x => x.Id).FirstAsync()).UserId);
        }

        using (var add = await client.PostAsJsonAsync("/api/v1/todo-extraction/todos", new ExtractedTodoAddRequestDto
        {
            ProjectId = world.ProjectId, MeetingId = world.MeetingId, Title = E2EFakeAi.ExtractedTodoTitle, Priority = "中",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            var todoId = (await ReadAsync<int>(add)).Data;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            Assert.Equal(world.MeetingId, (await db.Todo.AsNoTracking().SingleAsync(x => x.Id == todoId)).MeetingId);
            Assert.Contains("via=AiExtract", (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.TodoCreate && x.TargetId == todoId.ToString())).Detail);
        }

        var noTodoPermission = await SeedAsync([MagicObjectHelper.角色_專案項目, PermissionKey.For(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]);
        using (var other = await LoginAsync(noTodoPermission.Account))
        using (var response = await other.PostAsync($"/api/v1/todo-extraction/meetings/{noTodoPermission.MeetingId}", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Project
                .Where(x => x.Id == world.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProjectScopeRules.ArchivedStatus));
        }

        using (var archived = await client.PostAsync($"/api/v1/todo-extraction/meetings/{world.MeetingId}", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, archived.StatusCode);
            Assert.Equal(ProjectArchiveRules.ArchivedMessage, (await ReadAsync<TodoExtractionResultDto>(archived)).ErrorMessage);
        }
    }

    private sealed record World(string Account, int ProjectId, int MeetingId);

    /// <summary>使用者所在團隊主責、由他負責的專案，底下一場他上傳、已有會議紀錄的會議。</summary>
    private async Task<World> SeedAsync(string[] permissions)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, permissions);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"project-meeting-{suffix}", IsEnabled = true };
        db.UserTeam.Add(new UserTeam { MyUserId = userId, Team = team });
        var project = new Project { Title = $"專案會議-{suffix}", Status = "進行中", Owner = "負責人", OwnerUserId = userId };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        db.Project.Add(project);
        await db.SaveChangesAsync();
        var meeting = new Meeting
        {
            Title = $"會議-{suffix}", ProjectId = project.Id, CreatedByUserId = userId,
            DraftContent = "決議：下週上線。", DraftStatus = MeetingRecord.Share.Enums.DraftStatus.Completed,
            TranscriptionStatus = MeetingRecord.Share.Enums.TranscriptionStatus.Completed,
        };
        db.Meeting.Add(meeting);
        await db.SaveChangesAsync();
        return new World(account, project.Id, meeting.Id);
    }

    /// <summary>同一個主責團隊的另一個專案（使用者看得到），底下一場會議，回會議編號。</summary>
    private async Task<int> AddSiblingProjectMeetingAsync(int projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var teamId = await db.ProjectTeam.Where(x => x.ProjectId == projectId && x.IsPrimary).Select(x => x.TeamId).SingleAsync();
        var sibling = new Project { Title = $"兄弟專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "負責人" };
        sibling.Teams.Add(new ProjectTeam { TeamId = teamId, IsPrimary = true });
        var meeting = new Meeting { Title = $"兄弟會議-{Guid.NewGuid():N}", Project = sibling, DraftContent = "原本的內容" };
        db.Meeting.Add(meeting);
        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private async Task JoinTeamOfProjectAsync(int userId, int projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var teamId = await db.ProjectTeam.Where(x => x.ProjectId == projectId && x.IsPrimary).Select(x => x.TeamId).SingleAsync();
        db.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = teamId });
        await db.SaveChangesAsync();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<ApiResult<T>>(body, JsonOptions)!.Data!;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)!;

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }
}
