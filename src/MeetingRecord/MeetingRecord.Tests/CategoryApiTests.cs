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
/// WASM 遷移 M2（1.1.7）：分類 API 改走畫面用的 <c>CategoryService</c>，規則與畫面一致。
/// 這裡釘住外部呼叫端看得到的行為變更：名稱重複不分大小寫與前後空白、團隊同步（沒帶不動、空陣列清空）、
/// 啟用切換與稽核、搜尋的啟用篩選與排序。用 WASM 前端的路徑（登入 Cookie＋防偽標頭）呼叫。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class CategoryApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "category-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_NameDifferingOnlyByCaseOrSpaces_ShouldConflict()
    {
        using var client = await LoginAsync();
        var name = $"Report-{Guid.NewGuid():N}";
        using (var first = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var duplicate = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = $"  {name.ToUpperInvariant()} " });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("分類名稱已存在，無法新增。", (await ReadAsync<CategoryDto>(duplicate)).ErrorMessage);
    }

    [Fact]
    public async Task CreateAndUpdate_Teams_ShouldSyncOnlyWhenTeamIdsSent()
    {
        var (teamA, teamB) = await SeedTeamsAsync();
        using var client = await LoginAsync();

        // 新增帶團隊：一併同步，回應帶團隊名稱。
        CategoryDto created;
        using (var response = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = $"團隊分類-{Guid.NewGuid():N}", TeamIds = [teamA.Id, teamB.Id] }))
        {
            created = (await ReadAsync<CategoryDto>(response)).Data!;
        }

        Assert.Equal([teamA.Name, teamB.Name], created.TeamNames);

        // 修改沒帶 teamIds（1.1.7 以前的呼叫端）：團隊保留。
        using (var response = await client.PutAsJsonAsync($"/api/v1/Category/{created.Id}", new CategoryCreateUpdateDto { Id = created.Id, Name = created.Name, Description = "改描述" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var afterKeep = (await ReadAsync<CategoryDto>(await client.GetAsync($"/api/v1/Category/{created.Id}"))).Data!;
        Assert.Equal("改描述", afterKeep.Description);
        Assert.Equal([teamA.Id, teamB.Id], afterKeep.TeamIds);

        // 帶空陣列：清空。
        using (var response = await client.PutAsJsonAsync($"/api/v1/Category/{created.Id}", new CategoryCreateUpdateDto { Id = created.Id, Name = created.Name, TeamIds = [] }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var afterClear = (await ReadAsync<CategoryDto>(await client.GetAsync($"/api/v1/Category/{created.Id}"))).Data!;
        Assert.Empty(afterClear.TeamIds!);
    }

    [Fact]
    public async Task Update_NameUsedByAnother_ShouldConflict_AndMissing_ShouldBeNotFound()
    {
        using var client = await LoginAsync();
        var taken = await CreateAsync(client, $"已用-{Guid.NewGuid():N}");
        var mine = await CreateAsync(client, $"我的-{Guid.NewGuid():N}");

        using (var conflict = await client.PutAsJsonAsync($"/api/v1/Category/{mine.Id}", new CategoryCreateUpdateDto { Id = mine.Id, Name = taken.Name.ToLowerInvariant() }))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("分類名稱已存在，無法修改。", (await ReadAsync<object>(conflict)).ErrorMessage);
        }

        using var missing = await client.PutAsJsonAsync("/api/v1/Category/999999", new CategoryCreateUpdateDto { Id = 999999, Name = $"不存在-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SetEnabled_ShouldToggleAndAudit()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_分類清單]);
        using var client = await LoginAsync(account);
        var category = await CreateAsync(client, $"切換-{Guid.NewGuid():N}");

        using (var response = await client.PutAsJsonAsync($"/api/v1/Category/{category.Id}/enabled", new SetEnabledRequestDto { IsEnabled = false }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False((await db.Category.AsNoTracking().SingleAsync(x => x.Id == category.Id)).IsEnabled);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.CategoryDisable && x.TargetId == category.Id.ToString());
        Assert.Equal(userId, audit.ActorUserId);
        Assert.EndsWith("via=Api", (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.CategoryCreate && x.TargetId == category.Id.ToString())).Detail);

        using var missing = await client.PutAsJsonAsync("/api/v1/Category/999999/enabled", new SetEnabledRequestDto { IsEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Search_ShouldFilterByEnabledAndSort()
    {
        using var client = await LoginAsync();
        var prefix = $"搜尋-{Guid.NewGuid():N}";
        var b = await CreateAsync(client, $"{prefix}-b");
        var a = await CreateAsync(client, $"{prefix}-a");
        var c = await CreateAsync(client, $"{prefix}-c");
        using (await client.PutAsJsonAsync($"/api/v1/Category/{c.Id}/enabled", new SetEnabledRequestDto { IsEnabled = false }))
        {
        }

        var enabledByName = await SearchAsync(client, new CategorySearchRequestDto { Keyword = prefix, IsEnabled = true, SortBy = "name" });
        Assert.Equal([a.Id, b.Id], enabledByName.Items.Select(x => x.Id));

        var allByNameDesc = await SearchAsync(client, new CategorySearchRequestDto { Keyword = prefix, SortBy = "NAME", SortDescending = true });
        Assert.Equal([c.Id, b.Id, a.Id], allByNameDesc.Items.Select(x => x.Id));
        Assert.Equal(3, allByNameDesc.TotalCount);

        var disabledOnly = await SearchAsync(client, new CategorySearchRequestDto { Keyword = prefix, IsEnabled = false });
        Assert.Equal([c.Id], disabledOnly.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task SelectableTeams_ShouldListOnlyEnabledTeams()
    {
        var (enabled, _) = await SeedTeamsAsync();
        Team disabled;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            disabled = new Team { Name = $"停用團隊-{Guid.NewGuid():N}", IsEnabled = false };
            db.Team.Add(disabled);
            await db.SaveChangesAsync();
        }

        using var client = await LoginAsync();
        var teams = (await ReadAsync<List<TeamOptionDto>>(await client.GetAsync("/api/v1/Category/teams/selectable"))).Data!;

        Assert.Contains(teams, x => x.Id == enabled.Id && x.Name == enabled.Name);
        Assert.DoesNotContain(teams, x => x.Id == disabled.Id);
    }

    private async Task<(Team A, Team B)> SeedTeamsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var a = new Team { Name = $"A團隊-{suffix}", IsEnabled = true };
        var b = new Team { Name = $"B團隊-{suffix}", IsEnabled = true };
        db.Team.AddRange(a, b);
        await db.SaveChangesAsync();
        return (a, b);
    }

    private static async Task<CategoryDto> CreateAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name });
        var result = await ReadAsync<CategoryDto>(response);
        Assert.True(result.Success, result.ErrorMessage);
        return result.Data!;
    }

    private static async Task<PagedResult<CategoryDto>> SearchAsync(HttpClient client, CategorySearchRequestDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Category/search", request);
        return (await ReadAsync<PagedResult<CategoryDto>>(response)).Data!;
    }

    private async Task<HttpClient> LoginAsync(string? account = null)
    {
        account ??= (await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_分類清單])).Account;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
