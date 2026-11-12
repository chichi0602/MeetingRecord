using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Users;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 使用者管理 API（<c>api/v1/User</c>，只限管理者，1.1.10）。
/// 釘住：只有管理者能用、回應不含任何密碼相關欄位、自己與 support 的保護（以前只在畫面上）、
/// 修改時畫面以外的欄位不被清掉（Google 綁定、上次選的專案）、替別人重設密碼要對方下次改、稽核的操作者是 API 呼叫者。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class UserApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "user-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task NonAdmin_ShouldBeForbidden_EvenWithUserManagementPermission()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        {
            anonymous.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
            using var response = await anonymous.PostAsJsonAsync("/api/v1/User/search", new UserSearchRequestDto());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_使用者管理]);
        using var client = await LoginAsync(account);

        using (var response = await client.PostAsJsonAsync("/api/v1/User/search", new UserSearchRequestDto()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var denied = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PermissionDenied && x.ActorUserId == userId);
        Assert.Equal($"缺少權限：管理者; via={AuditVia.Api}", denied.Detail);
    }

    [Fact]
    public async Task Responses_ShouldNotContainAnySecret()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var targetId = await CreateTargetUserAsync(googleId: "google-123");

        var single = await (await client.GetAsync($"/api/v1/User/{targetId}")).Content.ReadAsStringAsync();
        var list = await (await client.PostAsJsonAsync("/api/v1/User/search", new UserSearchRequestDto { PageSize = 50 })).Content.ReadAsStringAsync();

        foreach (var json in new[] { single, list })
        {
            foreach (var secret in new[] { "\"password\"", "\"salt\"", "\"twoFactorSecret\"", "\"googleId\"", "google-123" })
            {
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Create_ShouldRequirePassword_AndRejectDuplicateAccount()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var roleId = await AnyRoleIdAsync();
        var account = $"new-{Guid.NewGuid():N}";

        using (var noPassword = await client.PostAsJsonAsync("/api/v1/User", new UserCreateUpdateDto { Account = account, Name = "新同事", RoleViewId = roleId }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);
            Assert.Equal("新增使用者時必須輸入密碼。", (await ReadAsync<UserDto>(noPassword)).ErrorMessage);
        }

        using (var created = await client.PostAsJsonAsync("/api/v1/User", new UserCreateUpdateDto { Account = account, Password = "new-pass-123", Name = "新同事", RoleViewId = roleId }))
        {
            var dto = (await ReadAsync<UserDto>(created)).Data!;
            Assert.Equal(account, dto.Account);
            Assert.Null(dto.Password);
        }

        using var duplicate = await client.PostAsJsonAsync("/api/v1/User", new UserCreateUpdateDto { Account = account, Password = "x-pass-123", Name = "重複", RoleViewId = roleId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("帳號已存在，無法新增。", (await ReadAsync<UserDto>(duplicate)).ErrorMessage);
    }

    [Fact]
    public async Task Update_ShouldKeepFieldsOutsideTheForm_AndAssignmentsWhenNotSent()
    {
        var (admin, adminId) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var team = await CreateTeamAsync();
        var projectId = await CreateProjectAsync();
        var targetId = await CreateTargetUserAsync(googleId: "google-keep", lastProjectId: projectId, teamId: team.Id);
        var before = await LoadUserAsync(targetId);

        // 只改名字：額外角色、團隊沒帶（null），密碼留白。
        var dto = (await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{targetId}"))).Data!;
        using (var response = await client.PutAsJsonAsync($"/api/v1/User/{targetId}", ToUpdate(dto, x => { x.Name = "改過的名字"; x.TeamNames = null; x.AdditionalRoleIds = null; })))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var after = await LoadUserAsync(targetId);
        Assert.Equal("改過的名字", after.Name);
        Assert.Equal("google-keep", after.GoogleId);
        Assert.Equal("Google", after.OAuthProvider);
        Assert.Equal(projectId, after.LastProjectId);
        Assert.Equal(before.Password, after.Password);
        Assert.False(after.MustChangePassword);
        Assert.Equal([team.Name], (await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{targetId}"))).Data!.TeamNames);

        // 稽核的操作者是 API 呼叫者（1.1.10 以前從 API 呼叫時是空的）。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var audit = await db.AuditLog.AsNoTracking().Where(x => x.Action == AuditActions.UserUpdate && x.TargetId == targetId.ToString()).SingleAsync();
            Assert.Equal(adminId, audit.ActorUserId);
        }

        // 帶空陣列：清空團隊。
        using (var response = await client.PutAsJsonAsync($"/api/v1/User/{targetId}", ToUpdate(dto, x => { x.TeamNames = []; })))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty((await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{targetId}"))).Data!.TeamNames!);
    }

    [Fact]
    public async Task ResetPassword_ForOthers_ShouldRequireChange_ButNotForSelf()
    {
        var (admin, adminId) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var targetId = await CreateTargetUserAsync();

        var target = (await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{targetId}"))).Data!;
        using (await client.PutAsJsonAsync($"/api/v1/User/{targetId}", ToUpdate(target, x => x.Password = "reset-pass-123")))
        {
        }

        Assert.True((await LoadUserAsync(targetId)).MustChangePassword);

        // 改自己的：不設「須改密碼」（以前從 API 呼叫時操作者是空的，會把自己當成別人而鎖在改密碼頁）。
        var self = (await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{adminId}"))).Data!;
        using (var response = await client.PutAsJsonAsync($"/api/v1/User/{adminId}", ToUpdate(self, x => x.Password = "my-new-pass-123")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False((await LoadUserAsync(adminId)).MustChangePassword);
    }

    [Fact]
    public async Task SelfAndSupport_ShouldBeProtected()
    {
        var (admin, adminId) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        int supportId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            supportId = (await db.MyUser.AsNoTracking().SingleAsync(x => x.Account == MagicObjectHelper.開發者帳號)).Id;
        }

        await AssertRejectedAsync(await client.DeleteAsync($"/api/v1/User/{adminId}"), "不能刪除自己的帳號。");
        await AssertRejectedAsync(await client.PutAsJsonAsync($"/api/v1/User/{adminId}/status", new SetEnabledRequestDto { IsEnabled = false }), "不能停用自己的帳號。");

        var self = (await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/User/{adminId}"))).Data!;
        await AssertRejectedAsync(await client.PutAsJsonAsync($"/api/v1/User/{adminId}", ToUpdate(self, x => x.IsAdmin = false)), "不能拿掉自己的管理者身分。");
        await AssertRejectedAsync(await client.PutAsJsonAsync($"/api/v1/User/{adminId}", ToUpdate(self, x => x.Status = false)), "不能停用自己的帳號。");

        await AssertRejectedAsync(await client.DeleteAsync($"/api/v1/User/{supportId}"), "不能刪除 support 帳號。");
        await AssertRejectedAsync(await client.PutAsJsonAsync($"/api/v1/User/{supportId}/status", new SetEnabledRequestDto { IsEnabled = false }), "不能停用 support 帳號。");

        var adminAfter = await LoadUserAsync(adminId);
        Assert.True(adminAfter.Status);
        Assert.True(adminAfter.IsAdmin);
        Assert.True((await LoadUserAsync(supportId)).Status);
    }

    [Fact]
    public async Task BatchTeam_ShouldReturnChangedCount()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var team = await CreateTeamAsync();
        var a = await CreateTargetUserAsync();
        var b = await CreateTargetUserAsync(teamId: team.Id);

        var added = await ReadAsync<int>(await client.PostAsJsonAsync("/api/v1/User/teams/batch", new UserTeamBatchRequestDto { UserIds = [a, b], TeamId = team.Id, Add = true }));
        Assert.Equal(1, added.Data);

        var removed = await ReadAsync<int>(await client.PostAsJsonAsync("/api/v1/User/teams/batch", new UserTeamBatchRequestDto { UserIds = [a, b], TeamId = team.Id, Add = false }));
        Assert.Equal(2, removed.Data);

        using var missing = await client.PostAsJsonAsync("/api/v1/User/teams/batch", new UserTeamBatchRequestDto { UserIds = [a], TeamId = 999999, Add = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Roles_ShouldFlagTheDefaultRole()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            if (!await db.RoleView.AnyAsync(x => x.Name == MagicObjectHelper.預設角色))
            {
                db.RoleView.Add(new RoleView { Name = MagicObjectHelper.預設角色, TabViewJson = "[]" });
                await db.SaveChangesAsync();
            }
        }

        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var roles = (await ReadAsync<List<RoleOptionDto>>(await client.GetAsync("/api/v1/User/roles"))).Data!;

        Assert.Equal(MagicObjectHelper.預設角色, roles.Single(x => x.IsDefault).Name);
    }

    private static UserCreateUpdateDto ToUpdate(UserDto dto, Action<UserCreateUpdateDto> change)
    {
        var update = new UserCreateUpdateDto
        {
            Id = dto.Id,
            Account = dto.Account,
            Name = dto.Name,
            Email = dto.Email,
            Status = dto.Status,
            IsAdmin = dto.IsAdmin,
            IsExternal = dto.IsExternal,
            RoleViewId = dto.RoleViewId,
            AdditionalRoleIds = dto.AdditionalRoleIds,
            TeamNames = dto.TeamNames,
            Password = string.Empty,
        };
        change(update);
        return update;
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string message)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(message, (await ReadAsync<object>(response)).ErrorMessage);
        }
    }

    private async Task<(string Account, int UserId)> CreateAdminAsync()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        (await db.MyUser.SingleAsync(x => x.Id == userId)).IsAdmin = true;
        await db.SaveChangesAsync();
        return (account, userId);
    }

    private async Task<int> CreateTargetUserAsync(string? googleId = null, int? lastProjectId = null, int? teamId = null)
    {
        var (_, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var user = await db.MyUser.SingleAsync(x => x.Id == userId);
        user.GoogleId = googleId;
        user.OAuthProvider = googleId is null ? null : "Google";
        user.LastProjectId = lastProjectId;
        if (teamId is { } id)
        {
            db.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = id });
        }

        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<Team> CreateTeamAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"使用者測試團隊-{Guid.NewGuid():N}", IsEnabled = true };
        db.Team.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private async Task<int> CreateProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var project = new Project { Title = $"上次選的專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "someone" };
        db.Project.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> AnyRoleIdAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        return await db.RoleView.AsNoTracking().Select(x => x.Id).FirstAsync();
    }

    private async Task<MyUser> LoadUserAsync(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        return await db.MyUser.AsNoTracking().SingleAsync(x => x.Id == userId);
    }

    private async Task<HttpClient> LoginAsync(string account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
