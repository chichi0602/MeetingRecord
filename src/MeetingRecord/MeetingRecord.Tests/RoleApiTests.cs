using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Roles;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M2（1.1.11）：角色管理 API（<c>api/v1/Role</c>，只限管理者）。
/// 釘住：只有管理者能用、權限樹轉成正確的權限鍵（整頁與只勾部分動作）、目錄外的名稱擋下（以前只有畫面在把關）、
/// 刪除有人用的角色時那些人改用其他角色、建立預設角色可重複呼叫、稽核的操作者是 API 呼叫者。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class RoleApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "role-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task NonAdmin_ShouldBeForbidden_EvenWithRoleManagementPermission()
    {
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await anonymous.GetAsync("/api/v1/Role/permission-template"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_角色管理]);
        using var client = await LoginAsync(account);
        using (var response = await client.PostAsJsonAsync("/api/v1/Role/search", new RoleSearchRequestDto()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.Equal($"缺少權限：管理者; via={AuditVia.Api}", (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PermissionDenied && x.ActorUserId == userId)).Detail);
    }

    [Fact]
    public async Task Create_ShouldTurnTreeIntoPermissionKeys_AndReadBackTheSameTree()
    {
        var (admin, adminId) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var tree = await TemplateAsync(client);
        var group = tree.Groups.Single(x => x.Name == MagicObjectHelper.角色_資料定義);
        group.Enable = true;
        group.Permissions.Single(x => x.Name == MagicObjectHelper.角色_分類清單).Actions[PermissionActions.View] = true;
        group.Permissions.Single(x => x.Name == MagicObjectHelper.角色_提示詞清單).Enable = true;

        var name = $"API角色-{Guid.NewGuid():N}";
        RoleDto created;
        using (var response = await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = name, RolePermission = tree }))
        {
            created = (await ReadAsync<RoleDto>(response)).Data!;
        }

        string[] expected = [MagicObjectHelper.角色_資料定義, PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View), MagicObjectHelper.角色_提示詞清單];
        Assert.Equal(expected.Order(StringComparer.Ordinal), (await StoredKeysAsync(created.Id)).Order(StringComparer.Ordinal));

        // 讀回的樹勾選一致。
        var readBack = (await ReadAsync<RoleDto>(await client.GetAsync($"/api/v1/Role/{created.Id}"))).Data!.RolePermission!;
        var readGroup = readBack.Groups.Single(x => x.Name == MagicObjectHelper.角色_資料定義);
        Assert.True(readGroup.Enable);
        Assert.True(readGroup.Permissions.Single(x => x.Name == MagicObjectHelper.角色_分類清單).Actions[PermissionActions.View]);
        Assert.False(readGroup.Permissions.Single(x => x.Name == MagicObjectHelper.角色_分類清單).Actions[PermissionActions.Create]);
        Assert.True(readGroup.Permissions.Single(x => x.Name == MagicObjectHelper.角色_提示詞清單).Enable);

        // 稽核的操作者是 API 呼叫者（1.1.11 以前從 API 呼叫時是空的）。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.Equal(adminId, (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.RoleCreate && x.TargetId == created.Id.ToString())).ActorUserId);
    }

    [Fact]
    public async Task Tree_WithNamesOutsideTheCatalog_ShouldBeRejected()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);

        // 目錄外的頁面（例如只限管理者的「系統健康監控」不能勾給角色）。
        var withUnknownPage = await TemplateAsync(client);
        withUnknownPage.Groups[0].Permissions.Add(new RolePermissionNodeDto { Name = "不存在的頁面", Enable = true });
        var name = $"壞角色-{Guid.NewGuid():N}";
        using (var response = await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = name, RolePermission = withUnknownPage }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("權限項目「不存在的頁面」不在權限目錄裡。", (await ReadAsync<RoleDto>(response)).ErrorMessage);
        }

        var withUnknownAction = await TemplateAsync(client);
        var node = withUnknownAction.Groups.SelectMany(x => x.Permissions).First(x => x.Name == MagicObjectHelper.角色_分類清單);
        node.Actions["approve"] = true;
        using (var response = await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = name, RolePermission = withUnknownAction }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal($"權限項目「{PermissionKey.For(MagicObjectHelper.角色_分類清單, "approve")}」不在權限目錄裡。", (await ReadAsync<RoleDto>(response)).ErrorMessage);
        }

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BackendDBContext>().RoleView.AnyAsync(x => x.Name == name));
    }

    [Fact]
    public async Task Create_DuplicateName_ShouldConflict()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        var name = $"重複角色-{Guid.NewGuid():N}";
        using (await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = name }))
        {
        }

        using var duplicate = await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = name });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("角色名稱已存在，無法新增。", (await ReadAsync<RoleDto>(duplicate)).ErrorMessage);
    }

    [Fact]
    public async Task Template_ShouldListTheWholeCatalogUnchecked()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);

        var tree = await TemplateAsync(client);

        var catalog = new RolePermissionService().GetRoleListPermissionAllName();
        Assert.Equal(catalog.Select(x => x[0]), tree.Groups.Select(x => x.Name));
        Assert.Equal(catalog.SelectMany(x => x.Skip(1)), tree.Groups.SelectMany(x => x.Permissions).Select(x => x.Name));
        Assert.DoesNotContain(tree.Groups, x => x.Enable || x.Permissions.Any(p => p.Enable || p.Actions.Values.Any(v => v)));
    }

    [Fact]
    public async Task Delete_RoleInUse_ShouldMoveItsUsersToAnotherRole()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);
        RoleDto role;
        using (var response = await client.PostAsJsonAsync("/api/v1/Role", new RoleDto { Name = $"將刪除-{Guid.NewGuid():N}" }))
        {
            role = (await ReadAsync<RoleDto>(response)).Data!;
        }

        int userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var user = new MyUser { Account = $"role-user-{Guid.NewGuid():N}", Name = "掛著角色的人", Status = true, RoleViewId = role.Id, Password = SecurePasswordHasher.HashPassword(Password) };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            db.UserRole.Add(new UserRole { MyUserId = user.Id, RoleViewId = role.Id });
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        Assert.Equal(1, (await ReadAsync<int>(await client.GetAsync($"/api/v1/Role/{role.Id}/user-count"))).Data);

        using (var response = await client.DeleteAsync($"/api/v1/Role/{role.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var user = await db.MyUser.AsNoTracking().SingleAsync(x => x.Id == userId);
            Assert.NotNull(user.RoleViewId);
            Assert.NotEqual(role.Id, user.RoleViewId);
            Assert.False(await db.RoleView.AnyAsync(x => x.Id == role.Id));
        }
    }

    [Fact]
    public async Task Presets_ShouldBeIdempotent()
    {
        var (admin, _) = await CreateAdminAsync();
        using var client = await LoginAsync(admin);

        using (await client.PostAsync("/api/v1/Role/presets", content: null))
        {
        }

        using var second = await client.PostAsync("/api/v1/Role/presets", content: null);
        Assert.Equal($"已新增 0 個預設角色，略過 {RolePresets.All.Count} 個（已有同名角色）。", (await ReadAsync<object>(second)).Message);
    }

    private async Task<List<string>> StoredKeysAsync(int roleId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        return await db.RolePermissionMap.AsNoTracking()
            .Where(x => x.RoleViewId == roleId)
            .Join(db.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();
    }

    private static async Task<RolePermissionDto> TemplateAsync(HttpClient client)
        => (await ReadAsync<RolePermissionDto>(await client.GetAsync("/api/v1/Role/permission-template"))).Data!;

    private async Task<(string Account, int UserId)> CreateAdminAsync()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_會議紀錄]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        (await db.MyUser.SingleAsync(x => x.Id == userId)).IsAdmin = true;
        await db.SaveChangesAsync();
        return (account, userId);
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
