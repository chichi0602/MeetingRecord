using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Auths;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 分類、提示詞、團隊、會議四組 Controller 的 HTTP 授權測試（1.0.58）。
/// 放在 <see cref="ApiIntegrationTests"/> 的 partial 裡共用同一個 factory：
/// factory 會設行程層級的環境變數，多開一個平行執行的 factory 會互相蓋掉（見 LoginCookie 測試的註解）。
/// <para>
/// 每支端點三件事：沒權杖 401；角色有該頁其他所有動作、唯獨缺這個動作時 403（並留稽核）；
/// 角色只有這一個動作時成功。第二點同時證明 [HasPermission] 用的是正確的動作鍵。
/// </para>
/// </summary>
public sealed partial class ApiIntegrationTests
{
    private static readonly string[] AllActions =
    [
        PermissionActions.View,
        PermissionActions.Create,
        PermissionActions.Edit,
        PermissionActions.Delete,
        PermissionActions.Export,
    ];

    /// <summary>端點代號 → (頁面權限鍵, 需要的動作)。會議、專案、待辦的刪除只要求 edit（負責人或上傳者可能沒有 delete，1.0.52）。</summary>
    private static readonly Dictionary<string, (string Page, string Action)> GuardedEndpoints = new(StringComparer.Ordinal)
    {
        ["Category.GetById"] = (MagicObjectHelper.角色_分類清單, PermissionActions.View),
        ["Category.Search"] = (MagicObjectHelper.角色_分類清單, PermissionActions.View),
        ["Category.Create"] = (MagicObjectHelper.角色_分類清單, PermissionActions.Create),
        ["Category.Update"] = (MagicObjectHelper.角色_分類清單, PermissionActions.Edit),
        ["Category.Delete"] = (MagicObjectHelper.角色_分類清單, PermissionActions.Delete),
        ["Category.SetEnabled"] = (MagicObjectHelper.角色_分類清單, PermissionActions.Edit),
        ["Category.SelectableTeams"] = (MagicObjectHelper.角色_分類清單, PermissionActions.View),
        ["PromptTemplate.GetById"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.View),
        ["PromptTemplate.Search"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.View),
        ["PromptTemplate.Create"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Create),
        ["PromptTemplate.Update"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Edit),
        ["PromptTemplate.Delete"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Delete),
        ["Team.GetById"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.View),
        ["Team.Search"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.View),
        ["Team.Create"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.Create),
        ["Team.Update"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.Edit),
        ["Team.Delete"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.Delete),
        ["Team.SetEnabled"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.Edit),
        ["Team.DeleteCheck"] = (MagicObjectHelper.角色_團隊清單, PermissionActions.Delete),
        ["Meeting.GetById"] = (MagicObjectHelper.角色_會議紀錄, PermissionActions.View),
        ["Meeting.Search"] = (MagicObjectHelper.角色_會議紀錄, PermissionActions.View),
        ["Meeting.Create"] = (MagicObjectHelper.角色_會議紀錄, PermissionActions.Create),
        ["Meeting.Update"] = (MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit),
        ["Meeting.Delete"] = (MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit),
    };

    public static TheoryData<string> GuardedEndpointNames => [.. GuardedEndpoints.Keys];

    [Theory]
    [MemberData(nameof(GuardedEndpointNames))]
    public async Task GuardedEndpoint_WithoutToken_ShouldReturnApiResult401(string endpoint)
    {
        using var client = factory.CreateClient();

        using var response = await SendEndpointAsync(client, endpoint, targetId: 1);
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
    }

    [Theory]
    [MemberData(nameof(GuardedEndpointNames))]
    public async Task GuardedEndpoint_MissingOnlyItsRequiredAction_ShouldReturnApiResult403AndAudit(string endpoint)
    {
        var (page, action) = GuardedEndpoints[endpoint];
        var everyOtherAction = AllActions.Where(a => a != action).Select(a => PermissionKey.For(page, a)).ToArray();
        var user = await CreateApiUserAsync(everyOtherAction);
        var targetId = await SeedTargetAsync(endpoint, user.UserId);
        using var client = await CreateBearerClientAsync(user);

        using var response = await SendEndpointAsync(client, endpoint, targetId);
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(403, result.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var denied = await db.AuditLog.AsNoTracking()
            .SingleAsync(x => x.Action == AuditActions.PermissionDenied && x.ActorAccount == user.Account);
        Assert.Equal("ApiEndpoint", denied.TargetType);
        Assert.Equal($"缺少權限：{PermissionKey.For(page, action)}; via=Api", denied.Detail);
    }

    [Theory]
    [MemberData(nameof(GuardedEndpointNames))]
    public async Task GuardedEndpoint_WithOnlyItsRequiredAction_ShouldSucceed(string endpoint)
    {
        var (page, action) = GuardedEndpoints[endpoint];
        var user = await CreateApiUserAsync(PermissionKey.For(page, action));
        var targetId = await SeedTargetAsync(endpoint, user.UserId);
        using var client = await CreateBearerClientAsync(user);

        using var response = await SendEndpointAsync(client, endpoint, targetId);
        var result = await ReadApiResultAsync<object>(response);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{endpoint} 回 {(int)response.StatusCode}：{result.Message}");
        Assert.True(result.Success);
    }

    /// <summary>
    /// 任一動作即可的端點（1.1.8，<c>[HasPermission(頁, anyOf)]</c>）：團隊的可選成員清單是全公司帳號，
    /// 只給能新增或修改團隊的人；只有檢視權限要 403 並留稽核。
    /// </summary>
    [Fact]
    public async Task TeamSelectableMembers_ShouldRequireCreateOrEdit()
    {
        const string url = "/api/Team/members/selectable";
        var page = MagicObjectHelper.角色_團隊清單;

        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var viewer = await CreateApiUserAsync(PermissionKey.For(page, PermissionActions.View), PermissionKey.For(page, PermissionActions.Delete));
        using (var client = await CreateBearerClientAsync(viewer))
        using (var response = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var denied = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PermissionDenied && x.ActorAccount == viewer.Account);
            Assert.Equal($"缺少權限：{PermissionKey.For(page, PermissionActions.Create)} 或 {PermissionKey.For(page, PermissionActions.Edit)}; via=Api", denied.Detail);
        }

        foreach (var action in new[] { PermissionActions.Create, PermissionActions.Edit })
        {
            var user = await CreateApiUserAsync(PermissionKey.For(page, action));
            using var client = await CreateBearerClientAsync(user);
            using var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"只有 {action} 時回 {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task MeetingApi_OtherTeamsProjectMeeting_ShouldBeNotFound_EvenWithFullPermissions()
    {
        // 看不到的當成不存在：有會議紀錄的全部動作權限，但會議屬於別的團隊的專案。
        var user = await CreateApiUserAsync(MagicObjectHelper.角色_會議紀錄);
        int hiddenMeetingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var otherTeam = new Team { Name = $"別的團隊-{Guid.NewGuid():N}", IsEnabled = true };
            var project = new Project { Title = $"別人的專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "someone" };
            project.Teams.Add(new ProjectTeam { Team = otherTeam, IsPrimary = true });
            var meeting = new Meeting { Title = $"別人的會議-{Guid.NewGuid():N}", Project = project };
            db.Meeting.Add(meeting);
            await db.SaveChangesAsync();
            hiddenMeetingId = meeting.Id;
        }

        await AssertMeetingHiddenAsync(user, hiddenMeetingId);
    }

    [Fact]
    public async Task MeetingApi_SomeoneElsesUnassignedMeeting_ShouldBeNotFound_EvenWithFullPermissions()
    {
        // 未歸屬專案的會議只有上傳者看得到。
        var user = await CreateApiUserAsync(MagicObjectHelper.角色_會議紀錄);
        var uploader = await CreateApiUserAsync(MagicObjectHelper.角色_會議紀錄);
        int hiddenMeetingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var meeting = new Meeting { Title = $"別人上傳的會議-{Guid.NewGuid():N}", CreatedByUserId = uploader.UserId };
            db.Meeting.Add(meeting);
            await db.SaveChangesAsync();
            hiddenMeetingId = meeting.Id;
        }

        await AssertMeetingHiddenAsync(user, hiddenMeetingId);
    }

    private async Task AssertMeetingHiddenAsync(ApiTestUser user, int hiddenMeetingId)
    {
        using var client = await CreateBearerClientAsync(user);

        using (var get = await client.GetAsync($"/api/Meeting/{hiddenMeetingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        }

        using (var put = await client.PutAsJsonAsync($"/api/Meeting/{hiddenMeetingId}", new MeetingCreateUpdateDto { Id = hiddenMeetingId, Title = "改名" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }

        using (var delete = await client.DeleteAsync($"/api/Meeting/{hiddenMeetingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        using var search = await client.PostAsJsonAsync("/api/Meeting/search", new MeetingSearchRequestDto { PageIndex = 1, PageSize = 100 });
        var page = await ReadApiResultAsync<PagedResult<MeetingDto>>(search);
        Assert.DoesNotContain(page.Data!.Items, x => x.Id == hiddenMeetingId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.NotEqual("改名", (await db.Meeting.AsNoTracking().SingleAsync(x => x.Id == hiddenMeetingId)).Title);
    }

    /// <summary>為端點準備一筆可操作的資料，回傳它的 Id；不需要 Id 的端點回 0。</summary>
    private async Task<int> SeedTargetAsync(string endpoint, int userId)
    {
        var (controller, action) = SplitEndpoint(endpoint);
        if (action is "Search" or "Create" or "SelectableTeams")
        {
            return 0;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var suffix = Guid.NewGuid().ToString("N");
        switch (controller)
        {
            case "Category":
            {
                var category = new Category { Name = $"分類-{suffix}" };
                db.Category.Add(category);
                await db.SaveChangesAsync();
                return category.Id;
            }
            case "PromptTemplate":
            {
                var prompt = new PromptTemplate { Name = $"提示詞-{suffix}", Content = "請整理會議紀錄。" };
                db.PromptTemplate.Add(prompt);
                await db.SaveChangesAsync();
                return prompt.Id;
            }
            case "Team":
            {
                var team = new Team { Name = $"團隊-{suffix}", IsEnabled = true };
                db.Team.Add(team);
                await db.SaveChangesAsync();
                return team.Id;
            }
            case "Meeting":
            {
                // 呼叫者自己上傳、未歸屬專案：看得到，也能刪（上傳者）。
                var meeting = new Meeting { Title = $"會議-{suffix}", CreatedByUserId = userId };
                db.Meeting.Add(meeting);
                await db.SaveChangesAsync();
                return meeting.Id;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null);
        }
    }

    private static Task<HttpResponseMessage> SendEndpointAsync(HttpClient client, string endpoint, int targetId)
    {
        var (controller, action) = SplitEndpoint(endpoint);
        var baseUrl = $"/api/{controller}";
        var suffix = Guid.NewGuid().ToString("N");

        return action switch
        {
            "GetById" => client.GetAsync($"{baseUrl}/{targetId}"),
            "Search" => client.PostAsJsonAsync($"{baseUrl}/search", new { pageIndex = 1, pageSize = 10 }),
            "Create" => client.PostAsJsonAsync(baseUrl, CreateBody(controller, id: 0, suffix)),
            "Update" => client.PutAsJsonAsync($"{baseUrl}/{targetId}", CreateBody(controller, targetId, suffix)),
            "Delete" => client.DeleteAsync($"{baseUrl}/{targetId}"),
            "SetEnabled" => client.PutAsJsonAsync($"{baseUrl}/{targetId}/enabled", new { isEnabled = false }),
            "SelectableTeams" => client.GetAsync($"{baseUrl}/teams/selectable"),
            "DeleteCheck" => client.GetAsync($"{baseUrl}/{targetId}/delete-check"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };
    }

    private static object CreateBody(string controller, int id, string suffix) => controller switch
    {
        "Category" => new CategoryCreateUpdateDto { Id = id, Name = $"分類-{suffix}" },
        "PromptTemplate" => new PromptTemplateCreateUpdateDto { Id = id, Name = $"提示詞-{suffix}", Content = "請整理會議紀錄。" },
        "Team" => new TeamCreateUpdateDto { Id = id, Name = $"團隊-{suffix}" },
        "Meeting" => new MeetingCreateUpdateDto { Id = id, Title = $"會議-{suffix}" },
        _ => throw new ArgumentOutOfRangeException(nameof(controller), controller, null),
    };

    private static (string Controller, string Action) SplitEndpoint(string endpoint)
    {
        var parts = endpoint.Split('.');
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// 建立一個非管理者帳號，角色只帶指定的權限鍵（可傳裸頁面鍵＝該頁全部動作）。
    /// 新的 API 授權測試一律用這個，不要再各自內嵌一份建角色、建帳號的程式。
    /// </summary>
    private async Task<ApiTestUser> CreateApiUserAsync(params string[] permissionKeys)
    {
        var account = $"api-{Guid.NewGuid():N}";
        const string password = "api-test-pass";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();

        var role = new RoleView { Name = $"API測試-{Guid.NewGuid():N}", TabViewJson = "[]" };
        db.RoleView.Add(role);
        await db.SaveChangesAsync();
        await writer.SyncRolePermissionsAsync(role.Id, permissionKeys);

        var user = new MyUser
        {
            Account = account,
            Name = account,
            Status = true,
            IsAdmin = false,
            RoleViewId = role.Id,
            Password = SecurePasswordHasher.HashPassword(password),
        };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);

        return new ApiTestUser(user.Id, account, password);
    }

    private async Task<HttpClient> CreateBearerClientAsync(ApiTestUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = user.Password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        Assert.True(loginResult.Success, $"測試帳號登入失敗：{loginResult.Message}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
        return client;
    }

    private sealed record ApiTestUser(int UserId, string Account, string Password);
}
