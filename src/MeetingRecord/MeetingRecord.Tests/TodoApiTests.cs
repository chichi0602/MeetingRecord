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
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 遷移 M3（1.1.18）：待辦 API 改走 <see cref="TodoService"/> 之後，HTTP 層的狀態碼與畫面用端點。
/// 規則本身（改期限原因、只限負責人刪除、封存、子項目、甲方）在 <c>TodoChangeAndNotificationTests</c> 逐條測；這裡驗 API 怎麼回。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class TodoApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "todo-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] AllTodoKeys = [MagicObjectHelper.角色_待辦事項];

    [Fact]
    public async Task Tree_ShouldNestChildren_AndFlagWhatTheCallerCanDelete()
    {
        var world = await SeedAsync();
        using var owner = await LoginAsync(world.OwnerAccount);
        using var member = await LoginAsync(world.MemberAccount);

        var ownerPage = await TreeAsync(owner, world.ProjectId);
        var parent = Assert.Single(ownerPage.Items, x => x.Id == world.ParentId);
        var child = Assert.Single(parent.Children!);
        Assert.Equal(world.ChildId, child.Id);
        Assert.Equal(1, parent.ChildrenTotal);
        Assert.DoesNotContain(ownerPage.Items, x => x.Id == world.ChildId); // 子項目只跟著主待辦出現
        Assert.True(parent.CanDelete);
        Assert.True(child.CanDelete);

        // 同專案、不是負責人：看得到、不能刪。
        var memberParent = Assert.Single((await TreeAsync(member, world.ProjectId)).Items, x => x.Id == world.ParentId);
        Assert.False(memberParent.CanDelete);

        // 對外的平面搜尋形狀不變：子項目也是一筆。
        using var search = await owner.PostAsJsonAsync("/api/Todo/search", new TodoSearchRequestDto { ProjectId = world.ProjectId, PageIndex = 1, PageSize = 50 });
        var flat = (await ReadAsync<PagedResult<TodoDto>>(search)).Data!;
        Assert.Contains(flat.Items, x => x.Id == world.ChildId && x.ParentTitle == "主待辦");
    }

    [Fact]
    public async Task WriteRules_ShouldComeBackAs400WithTheServiceMessage()
    {
        var world = await SeedAsync();
        using var owner = await LoginAsync(world.OwnerAccount);
        using var member = await LoginAsync(world.MemberAccount);

        // 改期限沒附原因。
        var update = new TodoCreateUpdateDto
        {
            Id = world.ParentId, Title = "主待辦", ProjectId = world.ProjectId, Status = "待辦", Priority = "中", DueDate = new DateTime(2030, 1, 1),
        };
        await AssertBadRequestAsync(await owner.PutAsJsonAsync($"/api/Todo/{world.ParentId}", update), TodoChangeRecorder.DueDateNoteRequiredMessage);

        // 附了原因就過，異動紀錄記 via=Api。
        update.DueDateChangeNote = "對方延後";
        using (var ok = await owner.PutAsJsonAsync($"/api/Todo/{world.ParentId}", update))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var changes = await owner.GetAsync($"/api/v1/Todo/{world.ParentId}/changes"))
        {
            var change = Assert.Single((await ReadAsync<List<TodoChangeDto>>(changes)).Data!, x => x.Note == "對方延後");
            Assert.Equal(AuditVia.Api, change.Via);
        }

        // 不是負責人不能刪。
        await AssertBadRequestAsync(await member.DeleteAsync($"/api/Todo/{world.ParentId}"), ProjectOwnerRules.DeleteTodoMessage);

        // 勾選完成。
        using (var completed = await member.PutAsJsonAsync($"/api/v1/Todo/{world.ParentId}/completed", new TodoCompletedRequestDto { IsCompleted = true }))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        // 專案封存後唯讀。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.Project.Where(x => x.Id == world.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProjectAdapterModel.ArchivedStatus));
        }

        await AssertBadRequestAsync(
            await member.PutAsJsonAsync($"/api/v1/Todo/{world.ParentId}/completed", new TodoCompletedRequestDto { IsCompleted = false }),
            ProjectArchiveRules.ArchivedMessage);
        Assert.False(Assert.Single((await TreeAsync(owner, world.ProjectId, scope: "All")).Items, x => x.Id == world.ParentId).CanDelete);
        Assert.DoesNotContain((await TreeAsync(owner, world.ProjectId, scope: "Active")).Items, x => x.Id == world.ParentId);

        // 負責人刪除：先把專案改回進行中。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            await db.Project.Where(x => x.Id == world.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "進行中"));
        }

        using var deleted = await owner.DeleteAsync($"/api/Todo/{world.ParentId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task OwnerPanel_AndSelectableProjects_ShouldFollowTheCallersScope()
    {
        var world = await SeedAsync();
        using var owner = await LoginAsync(world.OwnerAccount);

        using (var owners = await owner.GetAsync($"/api/v1/Todo/owners?projectId={world.ProjectId}&scope=All"))
        {
            var summary = Assert.Single((await ReadAsync<List<TodoOwnerSummaryDto>>(owners)).Data!, x => x.Owner == "王小明");
            Assert.Equal(2, summary.Total);
        }

        using (var byOwner = await owner.GetAsync($"/api/v1/Todo/by-owner?owner={Uri.EscapeDataString("王小明")}&projectId={world.ProjectId}"))
        {
            Assert.Equal(2, (await ReadAsync<List<TodoDto>>(byOwner)).Data!.Count);
        }

        using var projects = await owner.GetAsync("/api/v1/Todo/projects/selectable");
        var list = (await ReadAsync<List<TodoProjectOptionDto>>(projects)).Data!;
        Assert.Contains(list, x => x.Id == world.ProjectId);
        Assert.DoesNotContain(list, x => x.Id == world.HiddenProjectId);
    }

    [Fact]
    public async Task ParentCandidates_ShouldNeedCreateOrEdit()
    {
        var world = await SeedAsync();
        var (viewOnly, viewOnlyId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_待辦事項, PermissionActions.View)]);
        var (createOnly, createOnlyId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]);
        var (editOnly, editOnlyId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [PermissionKey.For(MagicObjectHelper.角色_待辦事項, PermissionActions.Edit)]);
        await JoinTeamAsync(world.TeamId, viewOnlyId, createOnlyId, editOnlyId);
        var url = $"/api/v1/Todo/parent-candidates?projectId={world.ProjectId}";

        using (var client = await LoginAsync(viewOnly))
        using (var forbidden = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        foreach (var account in new[] { createOnly, editOnly })
        {
            using var client = await LoginAsync(account);
            using var ok = await client.GetAsync(url);
            var options = (await ReadAsync<List<TodoParentOptionDto>>(ok)).Data!;
            Assert.Contains(options, x => x.Id == world.ParentId);
            Assert.DoesNotContain(options, x => x.Id == world.ChildId); // 子項目不能當主待辦
        }
    }

    [Fact]
    public async Task HiddenTodo_ShouldBeNotFoundEverywhere()
    {
        var world = await SeedAsync();
        using var owner = await LoginAsync(world.OwnerAccount);
        var hidden = world.HiddenTodoId;

        using (var get = await owner.GetAsync($"/api/Todo/{hidden}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        }

        var body = new TodoCreateUpdateDto { Id = hidden, Title = "改名", ProjectId = world.HiddenProjectId, Status = "待辦", Priority = "中" };
        using (var put = await owner.PutAsJsonAsync($"/api/Todo/{hidden}", body))
        {
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }

        using (var delete = await owner.DeleteAsync($"/api/Todo/{hidden}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        using (var completed = await owner.PutAsJsonAsync($"/api/v1/Todo/{hidden}/completed", new TodoCompletedRequestDto { IsCompleted = true }))
        {
            Assert.Equal(HttpStatusCode.NotFound, completed.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var todo = await scope.ServiceProvider.GetRequiredService<BackendDBContext>().Todo.AsNoTracking().SingleAsync(x => x.Id == hidden);
        Assert.Equal("看不到的待辦", todo.Title);
        Assert.Equal("待辦", todo.Status);
    }

    private sealed record World(
        string OwnerAccount, string MemberAccount, int TeamId, int ProjectId, int ParentId, int ChildId, int HiddenProjectId, int HiddenTodoId);

    /// <summary>
    /// 一個專案（負責人＋同團隊成員）：主待辦與一個子項目，都由王小明負責；另一個團隊的專案與待辦，兩人都看不到。
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (ownerAccount, ownerId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, AllTodoKeys);
        var (memberAccount, memberId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, AllTodoKeys);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var team = new Team { Name = $"todo-api-{suffix}", IsEnabled = true };
        var otherTeam = new Team { Name = $"todo-api-other-{suffix}", IsEnabled = true };
        db.UserTeam.AddRange(new UserTeam { MyUserId = ownerId, Team = team }, new UserTeam { MyUserId = memberId, Team = team });
        var project = new Project { Title = $"todo-api-{suffix}", Status = "進行中", Owner = "負責人", OwnerUserId = ownerId };
        project.Teams.Add(new ProjectTeam { Team = team, IsPrimary = true });
        var hiddenProject = new Project { Title = $"todo-api-hidden-{suffix}", Status = "進行中", Owner = "別人" };
        hiddenProject.Teams.Add(new ProjectTeam { Team = otherTeam, IsPrimary = true });
        db.Project.AddRange(project, hiddenProject);
        await db.SaveChangesAsync();

        var parent = new Todo { Title = "主待辦", ProjectId = project.Id, Status = "待辦", Priority = "中", Owner = "王小明" };
        var hiddenTodo = new Todo { Title = "看不到的待辦", ProjectId = hiddenProject.Id, Status = "待辦", Priority = "中" };
        db.Todo.AddRange(parent, hiddenTodo);
        await db.SaveChangesAsync();
        var child = new Todo { Title = "子項目", ProjectId = project.Id, ParentTodoId = parent.Id, Status = "待辦", Priority = "中", Owner = "王小明" };
        db.Todo.Add(child);
        await db.SaveChangesAsync();
        return new World(ownerAccount, memberAccount, team.Id, project.Id, parent.Id, child.Id, hiddenProject.Id, hiddenTodo.Id);
    }

    private async Task JoinTeamAsync(int teamId, params int[] userIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        db.UserTeam.AddRange(userIds.Select(id => new UserTeam { MyUserId = id, TeamId = teamId }));
        await db.SaveChangesAsync();
    }

    private static async Task<PagedResult<TodoDto>> TreeAsync(HttpClient client, int projectId, string scope = "All")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/Todo/tree", new TodoTreeRequestDto { ProjectId = projectId, PageIndex = 1, PageSize = 50, ProjectScope = scope });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<TodoDto>>(response)).Data!;
    }

    private static async Task AssertBadRequestAsync(HttpResponseMessage response, string message)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            // 既有的新增／修改／刪除照舊放在 Errors["Validation"]（外部相容）；新端點放 ErrorMessage（M2 慣例）。
            var result = await ReadAsync<object>(response);
            Assert.Equal(message, result.ErrorMessage ?? result.Errors?["Validation"].FirstOrDefault());
        }
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
}
