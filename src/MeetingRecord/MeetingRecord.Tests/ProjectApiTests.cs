using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Projects;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Controllers;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M3（1.1.24）：專案 API 改走 <c>ProjectService</c>。既有端點的相容行為由 <c>ApiIntegrationTests</c> 的專案案例把關；
/// 這裡驗新的畫面端點：表單整筆存檔（含附件上傳、移除）、附件下載、權限旗標、上次選的專案、選擇器，以及「同名改成允許」。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class ProjectApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "project-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ExternalCreate_SameTitle_ShouldNowBeAllowed()
    {
        var (client, userId, teamId) = await CreateMemberAsync();
        using (client)
        {
            var title = $"同名專案-{Guid.NewGuid():N}";
            foreach (var _ in new[] { 1, 2 })
            {
                using var response = await client.PostAsJsonAsync("/api/v1/Project", new ProjectCreateUpdateDto
                {
                    Id = 0, Title = title, StartDate = DateTime.Today, EndDate = DateTime.Today, Status = "進行中",
                    OwnerUserId = userId, PrimaryTeamId = teamId,
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using var scope = factory.Services.CreateScope();
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Project.CountAsync(x => x.Title == title));
        }
    }

    [Fact]
    public async Task Form_CreateWithAttachment_ThenDownload_ShouldAuditViaApi()
    {
        var (client, userId, teamId) = await CreateMemberAsync();
        using (client)
        {
            var upload = await UploadAttachmentAsync(client, "規格.pdf", 1000);
            var projectId = await SaveFormAsync(client, Form(0, userId, teamId, upload));

            var project = await GetAsync<ProjectDto>(client, $"/api/v1/Project/{projectId}");
            var file = Assert.Single(project.Files);
            Assert.Equal("規格.pdf", file.OriginalFileName);
            Assert.True(project.IsProjectOwner);
            Assert.True(project.CanManageTeams);

            // 存檔成功後暫存檔刪掉、這趟上傳就查不到了。
            Assert.False(File.Exists(Path.Combine(PendingUploadTempFiles.DefaultDirectory, $"{upload:N}.tmp")));
            using (var status = await client.GetAsync($"/api/v1/uploads/{upload}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
            }

            using var download = await client.GetAsync($"/api/v1/Project/files/{file.Id}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(1000, (await download.Content.ReadAsByteArrayAsync()).Length);

            using var scope = factory.Services.CreateScope();
            var audit = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().AuditLog.AsNoTracking()
                .SingleAsync(x => x.Action == AuditActions.ProjectFileDownload && x.TargetId == projectId.ToString());
            Assert.EndsWith("via=Api", audit.Detail);
        }
    }

    /// <summary>表單是整筆覆寫（名單傳空就清空，同 Server 頁）；對外的 PUT 照舊「空的就保留」。</summary>
    [Fact]
    public async Task Form_Update_ShouldOverwriteLists_WhileExternalPutKeepsThem()
    {
        var (client, userId, teamId) = await CreateMemberAsync();
        using (client)
        {
            var form = Form(0, userId, teamId);
            form.GlossaryTerms = ["甲系統"];
            var projectId = await SaveFormAsync(client, form);

            using (var put = await client.PutAsJsonAsync($"/api/v1/Project/{projectId}", new ProjectCreateUpdateDto
            {
                Id = projectId, Title = form.Title, StartDate = DateTime.Today, EndDate = DateTime.Today, Status = "進行中",
            }))
            {
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            }

            Assert.Equal(["甲系統"], (await GetAsync<ProjectDto>(client, $"/api/v1/Project/{projectId}")).GlossaryTerms);

            form.Id = projectId;
            form.GlossaryTerms = [];
            using (var update = await client.PutAsJsonAsync($"/api/v1/Project/{projectId}/form", form))
            {
                Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            }

            Assert.Empty((await GetAsync<ProjectDto>(client, $"/api/v1/Project/{projectId}")).GlossaryTerms);
        }
    }

    /// <summary>附件只有專案負責人能移除（1.0.52，服務層把關）；同團隊、有修改權限的人也不行。旗標照實回報。</summary>
    [Fact]
    public async Task Form_RemoveFile_ByNonOwner_ShouldBeRejected()
    {
        var (owner, ownerId, teamId) = await CreateMemberAsync();
        var (member, _, _) = await CreateMemberAsync(teamId);
        using (owner)
        using (member)
        {
            var projectId = await SaveFormAsync(owner, Form(0, ownerId, teamId, await UploadAttachmentAsync(owner, "a.txt", 10)));
            var project = await GetAsync<ProjectDto>(member, $"/api/v1/Project/{projectId}");
            Assert.False(project.IsProjectOwner);
            Assert.True(project.CanManageTeams);

            var form = Form(projectId, ownerId, teamId);
            form.RemovedFileIds = [project.Files[0].Id];
            using var response = await member.PutAsJsonAsync($"/api/v1/Project/{projectId}/form", form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ProjectOwnerRules.DeleteFileMessage, (await ReadAsync<ProjectSaveResultDto>(response)).ErrorMessage);
            Assert.Single((await GetAsync<ProjectDto>(owner, $"/api/v1/Project/{projectId}")).Files);
        }
    }

    [Fact]
    public async Task Form_WithSomeoneElsesUpload_ShouldBeRejected()
    {
        var (owner, ownerId, teamId) = await CreateMemberAsync();
        var (other, _, _) = await CreateMemberAsync(teamId);
        using (owner)
        using (other)
        {
            var othersUpload = await UploadAttachmentAsync(other, "b.txt", 10);
            using var response = await owner.PostAsJsonAsync("/api/v1/Project/form", Form(0, ownerId, teamId, othersUpload));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith("找不到已上傳完成的附件", (await ReadAsync<ProjectSaveResultDto>(response)).ErrorMessage);
        }
    }

    [Fact]
    public async Task AttachmentUpload_TooLargeOrCancelled_ShouldNotLeaveAFile()
    {
        var (client, _, _) = await CreateMemberAsync();
        using (client)
        {
            using (var tooLarge = await client.PostAsJsonAsync("/api/v1/uploads/project-attachments", new UploadSessionRequestDto
            {
                Purpose = UploadPurposes.ProjectAttachment, FileName = "big.mp4", Size = MeetingRecord.Business.Services.DataAccess.ProjectService.MaxUploadFileSize + 1,
            }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
            }

            using var created = await client.PostAsJsonAsync("/api/v1/uploads/project-attachments", new UploadSessionRequestDto
            {
                Purpose = UploadPurposes.ProjectAttachment, FileName = "c.txt", Size = 3000,
            });
            var session = (await ReadAsync<UploadSessionDto>(created)).Data!;
            using (var cancel = await client.DeleteAsync($"/api/v1/uploads/{session.UploadId}"))
            {
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }

            Assert.Equal(UploadStates.Cancelled, (await WaitUntilEndedAsync(client, session.UploadId)).State);
            Assert.False(File.Exists(Path.Combine(PendingUploadTempFiles.DefaultDirectory, $"{session.UploadId:N}.tmp")));
        }
    }

    [Fact]
    public async Task LastSelectedAndSelectable_ShouldFollowAccessAndArchive()
    {
        var (client, userId, teamId) = await CreateMemberAsync();
        using (client)
        {
            var active = await SaveFormAsync(client, Form(0, userId, teamId));
            var archivedForm = Form(0, userId, teamId);
            archivedForm.Status = ProjectScopeRules.ArchivedStatus;
            var archived = await SaveFormAsync(client, archivedForm);

            var activeOnly = await GetAsync<List<ProjectDto>>(client, "/api/v1/Project/selectable");
            Assert.Contains(activeOnly, x => x.Id == active);
            Assert.DoesNotContain(activeOnly, x => x.Id == archived);
            var all = await GetAsync<List<ProjectDto>>(client, "/api/v1/Project/selectable?includeArchived=true");
            Assert.Contains(all, x => x.Id == archived && x.IsArchived);

            using (var save = await client.PutAsJsonAsync("/api/v1/Project/last-selected", new LastSelectedProjectDto { ProjectId = active }))
            {
                Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            }

            Assert.Equal(active, (await GetAsync<LastSelectedProjectDto>(client, "/api/v1/Project/last-selected")).ProjectId);
        }
    }

    private static ProjectFormDto Form(int id, int ownerUserId, int teamId, params Guid[] uploads) => new()
    {
        Id = id,
        Title = $"表單專案-{Guid.NewGuid():N}",
        Status = "進行中",
        OwnerUserId = ownerUserId,
        PrimaryTeamId = teamId,
        UploadIds = [.. uploads],
    };

    private static async Task<int> SaveFormAsync(HttpClient client, ProjectFormDto form)
    {
        using var response = form.Id == 0
            ? await client.PostAsJsonAsync("/api/v1/Project/form", form)
            : await client.PutAsJsonAsync($"/api/v1/Project/{form.Id}/form", form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<ApiResult<ProjectSaveResultDto>>(body, JsonOptions)!.Data!.ProjectId;
    }

    /// <summary>建工作階段、送一塊、等到傳完，回上傳編號。</summary>
    private static async Task<Guid> UploadAttachmentAsync(HttpClient client, string fileName, int size)
    {
        using var created = await client.PostAsJsonAsync("/api/v1/uploads/project-attachments", new UploadSessionRequestDto
        {
            Purpose = UploadPurposes.ProjectAttachment, FileName = fileName, ContentType = "application/octet-stream", Size = size,
        });
        var session = (await ReadAsync<UploadSessionDto>(created)).Data!;
        using (var chunk = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/uploads/{session.UploadId}/chunks/0") { Content = new ByteArrayContent(new byte[size]) })
        {
            chunk.Headers.Add(UploadChunkController.CsrfHeaderName, "1");
            using var response = await client.SendAsync(chunk);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(UploadStates.Succeeded, (await WaitUntilEndedAsync(client, session.UploadId)).State);
        return session.UploadId;
    }

    private static async Task<UploadStatusDto> WaitUntilEndedAsync(HttpClient client, Guid uploadId)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await GetAsync<UploadStatusDto>(client, $"/api/v1/uploads/{uploadId}");
            if (status.State != UploadStates.Uploading)
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("上傳一直沒有結束。");
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

    /// <summary>專案項目全部權限、屬於某個團隊的一般帳號（沒給團隊就新建一個），以 Cookie 登入（分塊上傳只認 Cookie）。</summary>
    private async Task<(HttpClient Client, int UserId, int TeamId)> CreateMemberAsync(int? teamId = null)
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_專案項目]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            if (teamId is null)
            {
                var team = new Team { Name = $"專案API團隊-{Guid.NewGuid():N}", IsEnabled = true };
                db.Team.Add(team);
                await db.SaveChangesAsync();
                teamId = team.Id;
            }

            db.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = teamId.Value });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return (client, userId, teamId.Value);
    }
}
