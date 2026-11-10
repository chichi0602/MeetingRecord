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
        ["PromptTemplate.SetEnabled"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Edit),
        ["PromptTemplate.Presets"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Create),
        ["PromptTemplate.AddPresets"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Create),
        ["PromptTemplate.AddPreset"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.Create),
        ["PromptTemplate.EnabledCategories"] = (MagicObjectHelper.角色_提示詞清單, PermissionActions.View),
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
        // 1.1.18：待辦改走 TodoService，並多了畫面用的端點。刪除只要 edit（負責人可能沒有 delete，1.0.52）。
        ["Todo.GetById"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.Search"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.Create"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.Create),
        ["Todo.Update"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.Edit),
        ["Todo.Delete"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.Edit),
        ["Todo.Tree"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.SetCompleted"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.Edit),
        ["Todo.Owners"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.ByOwner"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.Changes"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
        ["Todo.SelectableProjects"] = (MagicObjectHelper.角色_待辦事項, PermissionActions.View),
    };

    /// <summary>待辦授權測試：種子待辦 → 它的專案（修改時本文要帶專案）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> SeededTodoProjects = new();

    public static TheoryData<string> GuardedEndpointNames => [.. GuardedEndpoints.Keys];

    /// <summary>「只新增一個內建範本」授權測試用的範本名稱。</summary>
    private static string AuthorizationPresetName => PromptTemplatePresets.All[0].Name;

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
        if (controller == "Todo")
        {
            return await SeedTodoAsync(action, userId);
        }

        if (action is "Search" or "Create" or "SelectableTeams" or "Presets" or "AddPresets" or "EnabledCategories")
        {
            return 0;
        }

        if (action == "AddPreset")
        {
            // 單一內建範本：先確保那個名稱還不存在（其他測試可能已經建過全部內建範本），成功案例才不會撞名。
            using var cleanup = factory.Services.CreateScope();
            var cleanupDb = cleanup.ServiceProvider.GetRequiredService<BackendDBContext>();
            await cleanupDb.PromptTemplate.Where(x => x.Name == AuthorizationPresetName).ExecuteDeleteAsync();
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

    /// <summary>
    /// 待辦（1.1.18）：呼叫者所在團隊主責、而且由他負責的專案（刪除只限專案負責人），新增時回專案編號、其他回待辦編號。
    /// </summary>
    private async Task<int> SeedTodoAsync(string action, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var team = new Team { Name = $"待辦團隊-{suffix}", IsEnabled = true };
        db.UserTeam.Add(new UserTeam { MyUserId = userId, Team = team });
        var project = new Project { Title = $"待辦專案-{suffix}", Status = "進行中", Owner = "負責人", OwnerUserId = userId };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        db.Project.Add(project);
        await db.SaveChangesAsync();
        if (action == "Create")
        {
            return project.Id;
        }

        var todo = new Todo { Title = $"待辦-{suffix}", ProjectId = project.Id, Status = "待辦", Priority = "中", Owner = "王小明" };
        db.Todo.Add(todo);
        await db.SaveChangesAsync();
        SeededTodoProjects[todo.Id] = project.Id;
        return todo.Id;
    }

    private static Task<HttpResponseMessage> SendEndpointAsync(HttpClient client, string endpoint, int targetId)
    {
        var (controller, action) = SplitEndpoint(endpoint);
        var baseUrl = $"/api/{controller}";
        var suffix = Guid.NewGuid().ToString("N");
        if (controller == "Todo")
        {
            return SendTodoEndpointAsync(client, action, targetId, suffix);
        }

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
            "Presets" => client.GetAsync($"{baseUrl}/presets"),
            "AddPresets" => client.PostAsync($"{baseUrl}/presets", content: null),
            "AddPreset" => client.PostAsJsonAsync($"{baseUrl}/presets/single", new { name = AuthorizationPresetName }),
            "EnabledCategories" => client.GetAsync($"{baseUrl}/categories/enabled"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };
    }

    private static Task<HttpResponseMessage> SendTodoEndpointAsync(HttpClient client, string action, int targetId, string suffix)
    {
        const string baseUrl = "/api/Todo";
        TodoCreateUpdateDto Body(int id, int projectId) => new() { Id = id, Title = $"待辦-{suffix}", ProjectId = projectId, Status = "待辦", Priority = "中" };
        return action switch
        {
            "GetById" => client.GetAsync($"{baseUrl}/{targetId}"),
            "Search" => client.PostAsJsonAsync($"{baseUrl}/search", new { pageIndex = 1, pageSize = 10 }),
            // 沒權杖的 401 案例 targetId 是 1、沒有種子專案：帶 1 也只會在權限那一關就被擋下。
            "Create" => client.PostAsJsonAsync(baseUrl, Body(0, targetId)),
            "Update" => client.PutAsJsonAsync($"{baseUrl}/{targetId}", Body(targetId, SeededTodoProjects.GetValueOrDefault(targetId, 1))),
            "Delete" => client.DeleteAsync($"{baseUrl}/{targetId}"),
            "Tree" => client.PostAsJsonAsync($"{baseUrl}/tree", new { pageIndex = 1, pageSize = 10 }),
            "SetCompleted" => client.PutAsJsonAsync($"{baseUrl}/{targetId}/completed", new { isCompleted = true }),
            "Owners" => client.GetAsync($"{baseUrl}/owners"),
            "ByOwner" => client.GetAsync($"{baseUrl}/by-owner?owner=" + Uri.EscapeDataString("王小明")),
            "Changes" => client.GetAsync($"{baseUrl}/{targetId}/changes"),
            "SelectableProjects" => client.GetAsync($"{baseUrl}/projects/selectable"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
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
