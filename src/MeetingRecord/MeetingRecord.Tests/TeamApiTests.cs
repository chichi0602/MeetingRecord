using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 團隊 API（1.1.8）走畫面用的 <c>TeamService</c>，規則與畫面一致。
/// 釘住外部呼叫端看得到的行為變更：名稱與代號重複不分大小寫與前後空白、成員同步（沒帶不動、空陣列清空）、
/// 仍是專案主責團隊的不能刪（非管理者，含刪除前檢查端點）、啟用切換與稽核、搜尋篩選與排序、可選成員。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class TeamApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "team-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_NameOrCodeDifferingOnlyByCaseOrSpaces_ShouldConflict()
    {
        using var client = await LoginAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await CreateAsync(client, $"Sales-{suffix}", code: $"SL-{suffix}");

        using (var byName = await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = $" sales-{suffix.ToUpperInvariant()} " }))
        {
            Assert.Equal(HttpStatusCode.Conflict, byName.StatusCode);
            Assert.Equal("團隊名稱已存在，無法新增。", (await ReadAsync<TeamDto>(byName)).ErrorMessage);
        }

        using var byCode = await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = $"另一個-{suffix}", Code = $" sl-{suffix} " });
        Assert.Equal(HttpStatusCode.Conflict, byCode.StatusCode);
        Assert.Equal("團隊代號已存在，無法新增。", (await ReadAsync<TeamDto>(byCode)).ErrorMessage);
    }

    [Fact]
    public async Task CreateAndUpdate_Members_ShouldSyncOnlyWhenMemberIdsSent()
    {
        var (_, memberA) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        var (_, memberB) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var client = await LoginAsync();

        TeamDto created;
        using (var response = await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = $"成員團隊-{Guid.NewGuid():N}", MemberIds = [memberA, memberB] }))
        {
            created = (await ReadAsync<TeamDto>(response)).Data!;
        }

        Assert.Equal(2, created.MemberCount);

        // 修改沒帶 memberIds（1.1.8 以前的呼叫端）：成員保留。
        using (var response = await client.PutAsJsonAsync($"/api/v1/Team/{created.Id}", new TeamCreateUpdateDto { Id = created.Id, Name = created.Name, Description = "改描述" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var afterKeep = await GetAsync(client, created.Id!.Value);
        Assert.Equal("改描述", afterKeep.Description);
        Assert.Equal([memberA, memberB], afterKeep.MemberIds!.Order());

        // 帶空陣列：清空；搜尋結果的成員數跟著變。
        using (var response = await client.PutAsJsonAsync($"/api/v1/Team/{created.Id}", new TeamCreateUpdateDto { Id = created.Id, Name = created.Name, MemberIds = [] }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty((await GetAsync(client, created.Id.Value)).MemberIds!);
        var listed = await SearchAsync(client, new TeamSearchRequestDto { Keyword = created.Name });
        Assert.Equal(0, listed.Items.Single().MemberCount);
    }

    [Fact]
    public async Task Delete_PrimaryTeam_ShouldBeBlockedForNonAdmin_ButAllowedForAdmin()
    {
        using var client = await LoginAsync();
        var team = await CreateAsync(client, $"主責團隊-{Guid.NewGuid():N}");
        await MakePrimaryOfNewProjectAsync(team.Id!.Value);

        // 非管理者：刪除前檢查與刪除都回 409，團隊還在。
        using (var check = await client.GetAsync($"/api/v1/Team/{team.Id}/delete-check"))
        {
            Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace((await ReadAsync<object>(check)).ErrorMessage));
        }

        using (var delete = await client.DeleteAsync($"/api/v1/Team/{team.Id}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        }

        Assert.True(await TeamExistsAsync(team.Id.Value));

        // 管理者不受限（0.4.104）。
        using var admin = await LoginAsync(admin: true);
        using (var check = await admin.GetAsync($"/api/v1/Team/{team.Id}/delete-check"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        using (var delete = await admin.DeleteAsync($"/api/v1/Team/{team.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        Assert.False(await TeamExistsAsync(team.Id.Value));

        using var missing = await admin.GetAsync("/api/v1/Team/999999/delete-check");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SetEnabled_ShouldToggleAndAudit()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_團隊清單]);
        using var client = await LoginAsync(account);
        var team = await CreateAsync(client, $"切換團隊-{Guid.NewGuid():N}");

        using (var response = await client.PutAsJsonAsync($"/api/v1/Team/{team.Id}/enabled", new SetEnabledRequestDto { IsEnabled = false }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False((await db.Team.AsNoTracking().SingleAsync(x => x.Id == team.Id)).IsEnabled);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.TeamDisable && x.TargetId == team.Id.ToString());
        Assert.Equal(userId, audit.ActorUserId);
        Assert.EndsWith("via=Api", (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.TeamCreate && x.TargetId == team.Id.ToString())).Detail);
    }

    [Fact]
    public async Task Search_ShouldFilterByEnabledAndSortByCode()
    {
        using var client = await LoginAsync();
        var prefix = $"搜尋團隊-{Guid.NewGuid():N}";
        var b = await CreateAsync(client, $"{prefix}-1", code: $"{prefix}-B");
        var a = await CreateAsync(client, $"{prefix}-2", code: $"{prefix}-A");
        var c = await CreateAsync(client, $"{prefix}-3", code: $"{prefix}-C");
        using (await client.PutAsJsonAsync($"/api/v1/Team/{c.Id}/enabled", new SetEnabledRequestDto { IsEnabled = false }))
        {
        }

        var enabledByCode = await SearchAsync(client, new TeamSearchRequestDto { Keyword = prefix, IsEnabled = true, SortBy = "code" });
        Assert.Equal([a.Id, b.Id], enabledByCode.Items.Select(x => x.Id));

        var allByCodeDesc = await SearchAsync(client, new TeamSearchRequestDto { Keyword = prefix, SortBy = "Code", SortDescending = true });
        Assert.Equal([c.Id, b.Id, a.Id], allByCodeDesc.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task SelectableMembers_ShouldListOnlyEnabledAccounts()
    {
        var (enabledAccount, enabledId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        var (_, disabledId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var disabled = await db.MyUser.SingleAsync(x => x.Id == disabledId);
            disabled.Status = false;
            await db.SaveChangesAsync();
        }

        using var client = await LoginAsync();
        var members = (await ReadAsync<List<MemberOptionDto>>(await client.GetAsync("/api/v1/Team/members/selectable"))).Data!;

        Assert.Contains(members, x => x.Id == enabledId && x.Label.Contains($"（{enabledAccount}）"));
        Assert.DoesNotContain(members, x => x.Id == disabledId);
    }

    private async Task MakePrimaryOfNewProjectAsync(int teamId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var project = new Project { Title = $"用到主責的專案-{Guid.NewGuid():N}", Status = "進行中", Owner = "someone" };
        project.Teams.Add(new ProjectTeam { TeamId = teamId, IsPrimary = true });
        db.Project.Add(project);
        await db.SaveChangesAsync();
    }

    private async Task<bool> TeamExistsAsync(int teamId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        return await db.Team.AsNoTracking().AnyAsync(x => x.Id == teamId);
    }

    private static async Task<TeamDto> CreateAsync(HttpClient client, string name, string? code = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = name, Code = code });
        var result = await ReadAsync<TeamDto>(response);
        Assert.True(result.Success, result.ErrorMessage);
        return result.Data!;
    }

    private static async Task<TeamDto> GetAsync(HttpClient client, int id)
        => (await ReadAsync<TeamDto>(await client.GetAsync($"/api/v1/Team/{id}"))).Data!;

    private static async Task<PagedResult<TeamDto>> SearchAsync(HttpClient client, TeamSearchRequestDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Team/search", request);
        return (await ReadAsync<PagedResult<TeamDto>>(response)).Data!;
    }

    /// <summary>非管理者、只有團隊清單全部動作的帳號（或指定帳號）；<paramref name="admin"/> 時改成管理者。</summary>
    private async Task<HttpClient> LoginAsync(string? account = null, bool admin = false)
    {
        if (account is null)
        {
            var (created, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_團隊清單]);
            account = created;
            if (admin)
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
                var user = await db.MyUser.SingleAsync(x => x.Id == userId);
                user.IsAdmin = true;
                await db.SaveChangesAsync();
            }
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
