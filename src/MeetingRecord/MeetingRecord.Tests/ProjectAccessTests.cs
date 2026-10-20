using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 團隊權控（0.4.102）：團隊＝誰的資料，分類＝什麼資料，角色＝能做什麼。
/// 專案有一個主責團隊（必填）與 0～多個協作團隊；使用者所屬團隊與主責＋協作有交集才看得到；沒有公開專案；管理者看全部。
/// 會議那一半在 <c>MeetingServiceTests</c> 的「專案可見性」區塊。
/// </summary>
public sealed class ProjectAccessTests
{
    #region ProjectAccess 規則（純邏輯）

    [Fact]
    public void CanViewMeeting_UnassignedMeeting_ShouldOnlyBeVisibleToUploader()
    {
        var access = new ProjectAccess(false, 7, [1], [1]);

        Assert.True(access.CanViewMeeting(null, 7));
        Assert.False(access.CanViewMeeting(null, 8));
        // 沒有上傳者的舊會議：歸屬專案之前只有管理者看得到。
        Assert.False(access.CanViewMeeting(null, null));
        Assert.True(access.CanViewMeeting(1, null));
        Assert.False(access.CanViewMeeting(2, 7));
    }

    [Fact]
    public void UnresolvedUser_ShouldNotOwnUnassignedMeetings()
    {
        // 解析不到使用者（UserId=0）時，上傳者為 null 的會議不能被當成「自己的」。
        var access = new ProjectAccess(false, 0, [], []);

        Assert.False(access.CanViewMeeting(null, null));
        Assert.False(access.CanViewProject(1));
    }

    [Fact]
    public void Admin_ShouldPassEverything()
    {
        var access = new ProjectAccess(true, 1, [], []);

        Assert.True(access.CanViewProject(99));
        Assert.True(access.CanViewMeeting(null, null));
    }

    [Fact]
    public void ResolveProjectTeams_PrimaryIsRequired()
    {
        var access = new ProjectAccess(true, 1, [], []);

        Assert.NotNull(access.ResolveProjectTeams(null, null, [2]).Error);
    }

    [Fact]
    public void ResolveProjectTeams_NonAdmin_PrimaryMustBeOwnTeam_CollaboratorsCanBeAny()
    {
        // 使用者屬於 1、2。
        var access = new ProjectAccess(false, 7, [1, 2], []);

        Assert.NotNull(access.ResolveProjectTeams(null, 9, []).Error);

        var ok = access.ResolveProjectTeams(null, 1, [9, 1, 3]);
        Assert.Null(ok.Error);
        Assert.Equal(1, ok.PrimaryTeamId);
        // 主責不重複列在協作裡。
        Assert.Equal([9, 3], ok.CollaboratorTeamIds);
    }

    [Fact]
    public void ResolveProjectTeams_NonAdmin_UnchangedPrimaryIsKeptEvenIfNotOwn()
    {
        // 他是協作團隊的人：主責（9）不是他的團隊，但沒改主責就要能存檔。
        var access = new ProjectAccess(false, 7, [1], []);

        var result = access.ResolveProjectTeams(9, 9, [1]);

        Assert.Null(result.Error);
        Assert.Equal(9, result.PrimaryTeamId);
    }

    [Fact]
    public void ResolveProjectTeams_Admin_CanPickAnyPrimary()
    {
        var access = new ProjectAccess(true, 1, [], []);

        var result = access.ResolveProjectTeams(2, 9, []);

        Assert.Null(result.Error);
        Assert.Equal(9, result.PrimaryTeamId);
    }

    #endregion

    #region 專案可見性

    [Fact]
    public async Task WangXiaoMing_SeesProjectsWherePrimaryOrCollaboratorIsHisTeam()
    {
        // 使用者給的例子：王小明屬於研發部＋管理部。
        await using var fixture = await Fixture.CreateAsync();
        var wang = await fixture.AddUserAsync("wang");
        var rd = await fixture.AddTeamAsync("研發部", wang.Id);
        var admin = await fixture.AddTeamAsync("管理部", wang.Id);
        var sales = await fixture.AddTeamAsync("業務部");
        await fixture.AddProjectAsync("專案A", rd.Id);
        await fixture.AddProjectAsync("專案B", admin.Id);
        await fixture.AddProjectAsync("專案C", sales.Id);
        await fixture.AddProjectAsync("專案D", sales.Id, rd.Id);

        var projects = await fixture.CreateProjectService(wang.Id).GetSelectableAsync();

        Assert.Equal(["專案A", "專案B", "專案D"], projects.Select(x => x.Title).Order().ToList());
    }

    [Fact]
    public async Task ProjectWithoutTeams_IsNotPublic_OnlyAdminSeesIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        await fixture.AddTeamAsync("業務部", alice.Id);
        await fixture.AddProjectAsync("沒掛團隊的舊專案");

        Assert.Empty(await fixture.CreateProjectService(alice.Id).GetSelectableAsync());
        Assert.Single(await fixture.CreateProjectService(0, isAdmin: true).GetSelectableAsync());
    }

    [Fact]
    public async Task AddProject_NonAdmin_OwnPrimaryAndAnyCollaborator_SavesTeamsAndCategories()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var rd = await fixture.AddTeamAsync("研發部", alice.Id);
        var sales = await fixture.AddTeamAsync("業務部");

        var result = await fixture.CreateProjectService(alice.Id).AddAsync(new ProjectAdapterModel
        {
            Title = "成大醫療平台",
            OwnerUserId = alice.Id,
            PrimaryTeamId = rd.Id,
            CollaboratorTeamIds = [sales.Id],
            Categories = [".NET", "成大"],
        });

        Assert.True(result.Success);
        var project = await fixture.Context.Project.AsNoTracking().Include(x => x.Teams).SingleAsync();
        Assert.Equal(rd.Id, project.Teams.Single(x => x.IsPrimary).TeamId);
        Assert.Equal([sales.Id], project.Teams.Where(x => !x.IsPrimary).Select(x => x.TeamId).ToList());
        // 1.0.52：負責人是帳號，姓名快照由服務填入。
        Assert.Equal(alice.Id, project.OwnerUserId);
        Assert.Equal("alice", project.Owner);

        var model = await fixture.CreateProjectService(alice.Id).GetAsync(project.Id);
        Assert.Equal([".NET", "成大"], model.Categories);
        Assert.Equal("研發部", model.PrimaryTeamName);
        Assert.Equal(["業務部"], model.CollaboratorTeamNames);
    }

    [Fact]
    public async Task AddProject_NonAdmin_PrimaryOfOthersTeam_OrMissing_ShouldFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        await fixture.AddTeamAsync("研發部", alice.Id);
        var sales = await fixture.AddTeamAsync("業務部");
        var service = fixture.CreateProjectService(alice.Id);

        var othersPrimary = await service.AddAsync(new ProjectAdapterModel { Title = "A", Owner = "x", PrimaryTeamId = sales.Id });
        var missing = await service.AddAsync(new ProjectAdapterModel { Title = "B", Owner = "x" });

        Assert.False(othersPrimary.Success);
        Assert.False(missing.Success);
        Assert.False(await fixture.Context.Project.AnyAsync());
    }

    [Fact]
    public async Task UpdateProject_CollaboratorTeamMember_CanEditContent_ButNotTeams()
    {
        // 1.0.47：協作團隊的人可以改專案內容，但團隊設定只有主責團隊的成員能改。
        await using var fixture = await Fixture.CreateAsync();
        var bob = await fixture.AddUserAsync("bob");
        var sales = await fixture.AddTeamAsync("業務部");
        var rd = await fixture.AddTeamAsync("研發部", bob.Id);
        var admin = await fixture.AddTeamAsync("管理部");
        var project = await fixture.AddProjectAsync("共用專案", sales.Id, rd.Id);
        var service = fixture.CreateProjectService(bob.Id);

        var model = await service.GetAsync(project.Id);
        model.Categories = ["Java"];
        Assert.True((await service.UpdateAsync(model)).Success);
        Assert.Equal(["Java"], (await service.GetAsync(project.Id)).Categories);

        var addCollaborator = await service.GetAsync(project.Id);
        addCollaborator.CollaboratorTeamIds = [rd.Id, admin.Id];
        var takeOverPrimary = await service.GetAsync(project.Id);
        takeOverPrimary.PrimaryTeamId = rd.Id;
        takeOverPrimary.CollaboratorTeamIds = [];

        Assert.Equal(ProjectAccess.ProjectTeamManageMessage, (await service.UpdateAsync(addCollaborator)).Message);
        Assert.Equal(ProjectAccess.ProjectTeamManageMessage, (await service.UpdateAsync(takeOverPrimary)).Message);

        var links = await fixture.Context.ProjectTeam.AsNoTracking().ToListAsync();
        Assert.Equal(sales.Id, links.Single(x => x.IsPrimary).TeamId);
        Assert.Equal([rd.Id], links.Where(x => !x.IsPrimary).Select(x => x.TeamId).ToList());
    }

    [Fact]
    public async Task UpdateProject_PrimaryTeamMember_CanChangeTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var sales = await fixture.AddTeamAsync("業務部", alice.Id);
        var rd = await fixture.AddTeamAsync("研發部");
        var admin = await fixture.AddTeamAsync("管理部");
        var project = await fixture.AddProjectAsync("共用專案", sales.Id, rd.Id);
        var service = fixture.CreateProjectService(alice.Id);

        var model = await service.GetAsync(project.Id);
        model.CollaboratorTeamIds = [admin.Id];
        Assert.True((await service.UpdateAsync(model)).Success);

        var links = await fixture.Context.ProjectTeam.AsNoTracking().ToListAsync();
        Assert.Equal([admin.Id], links.Where(x => !x.IsPrimary).Select(x => x.TeamId).ToList());
    }

    [Fact]
    public async Task DeleteProject_OnlyOwnerOrAdmin_AndNotWhenArchived()
    {
        // 1.0.52：刪除專案只限負責人（原本是主責團隊成員）；主責團隊的其他人、協作團隊的人都不行。
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var carol = await fixture.AddUserAsync("carol");
        var sales = await fixture.AddTeamAsync("業務部", alice.Id, carol.Id);
        var rd = await fixture.AddTeamAsync("研發部", bob.Id);
        var shared = await fixture.AddProjectAsync("共用專案", sales.Id, rd.Id);
        var archived = await fixture.AddProjectAsync("已結案", sales.Id);
        await fixture.Context.Project.ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, alice.Id));
        await fixture.Context.Project.Where(x => x.Id == archived.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProjectAdapterModel.ArchivedStatus));

        var byCollaborator = await fixture.CreateProjectService(bob.Id).DeleteAsync(shared.Id);
        var byPrimaryMember = await fixture.CreateProjectService(carol.Id).DeleteAsync(shared.Id);
        var archivedByOwner = await fixture.CreateProjectService(alice.Id).DeleteAsync(archived.Id);

        Assert.Equal(ProjectOwnerRules.DeleteProjectMessage, byCollaborator.Message);
        Assert.Equal(ProjectOwnerRules.DeleteProjectMessage, byPrimaryMember.Message);
        Assert.Equal(ProjectArchiveRules.ArchivedMessage, archivedByOwner.Message);
        Assert.Equal(2, await fixture.Context.Project.CountAsync());

        Assert.True((await fixture.CreateProjectService(alice.Id).DeleteAsync(shared.Id)).Success);
        Assert.True((await fixture.CreateProjectService(0, isAdmin: true).DeleteAsync(archived.Id)).Success);
        Assert.False(await fixture.Context.Project.AnyAsync());
    }

    [Fact]
    public async Task OwnerCandidates_OnlyEnabledMembersOfProjectTeams()
    {
        // 1.0.52：負責人下拉只列主責＋協作團隊裡啟用中的帳號，同一人只列一次。
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var disabled = await fixture.AddUserAsync("disabled");
        var outsider = await fixture.AddUserAsync("outsider");
        await fixture.Context.MyUser.Where(x => x.Id == disabled.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, false));
        var rd = await fixture.AddTeamAsync("研發部", alice.Id, disabled.Id);
        var sales = await fixture.AddTeamAsync("業務部", alice.Id, bob.Id);
        await fixture.AddTeamAsync("人資部", outsider.Id);

        var candidates = await fixture.CreateProjectService(alice.Id).GetOwnerCandidatesAsync(rd.Id, [sales.Id]);

        Assert.Equal([alice.Id, bob.Id], candidates.Select(x => x.Id).Order().ToList());
        Assert.Equal("alice（alice）", candidates.Single(x => x.Id == alice.Id).Label);
    }

    [Fact]
    public async Task AddProject_OwnerMustBeEnabledTeamMember()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var outsider = await fixture.AddUserAsync("outsider");
        var rd = await fixture.AddTeamAsync("研發部", alice.Id);
        var service = fixture.CreateProjectService(alice.Id);

        var noOwner = await service.AddAsync(new ProjectAdapterModel { Title = "A", PrimaryTeamId = rd.Id });
        var outsiderOwner = await service.AddAsync(new ProjectAdapterModel { Title = "B", PrimaryTeamId = rd.Id, OwnerUserId = outsider.Id });

        Assert.Equal(ProjectOwnerRules.RequiredMessage, noOwner.Message);
        Assert.Equal(ProjectOwnerRules.InvalidCandidateMessage, outsiderOwner.Message);
        Assert.False(await fixture.Context.Project.AnyAsync());
    }

    [Fact]
    public async Task ChangeOwner_OnlyPrimaryMemberOrCurrentOwner_AndOnlyOwnerRemovesFiles()
    {
        // 協作團隊的人不能把自己設成負責人（不然就拿到刪除權）；主責團隊成員、目前的負責人可以換。
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var carol = await fixture.AddUserAsync("carol");
        var bob = await fixture.AddUserAsync("bob");
        var sales = await fixture.AddTeamAsync("業務部", alice.Id, carol.Id);
        var rd = await fixture.AddTeamAsync("研發部", bob.Id);
        var project = await fixture.AddProjectAsync("共用專案", sales.Id, rd.Id);
        await fixture.Context.Project.ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, alice.Id).SetProperty(x => x.Owner, "alice"));

        var bobService = fixture.CreateProjectService(bob.Id);
        var takeOver = await bobService.GetAsync(project.Id);
        takeOver.OwnerUserId = bob.Id;
        var removeFile = await bobService.GetAsync(project.Id);

        Assert.Equal(ProjectOwnerRules.ChangeDeniedMessage, (await bobService.UpdateAsync(takeOver)).Message);
        Assert.Equal(ProjectOwnerRules.DeleteFileMessage, (await bobService.UpdateAsync(removeFile, removedFileIds: [999])).Message);

        // 協作團隊的人照常可以改內容，負責人保留。
        var editTitle = await bobService.GetAsync(project.Id);
        editTitle.Title = "改名";
        Assert.True((await bobService.UpdateAsync(editTitle)).Success);

        var byOwner = await fixture.CreateProjectService(alice.Id).GetAsync(project.Id);
        byOwner.OwnerUserId = carol.Id;
        Assert.True((await fixture.CreateProjectService(alice.Id).UpdateAsync(byOwner)).Success);

        var saved = await fixture.Context.Project.AsNoTracking().SingleAsync();
        Assert.Equal(carol.Id, saved.OwnerUserId);
        Assert.Equal("carol", saved.Owner);
        Assert.Equal("改名", saved.Title);
    }

    [Fact]
    public async Task LastSelectedProject_IsRememberedPerUser_AndInvisibleProjectsAreIgnored()
    {
        // 1.0.51：專案頁記住上次選的專案，記在帳號上、每人一份；看不到的專案不記。
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var rd = await fixture.AddTeamAsync("研發部", alice.Id, bob.Id);
        var sales = await fixture.AddTeamAsync("業務部");
        var first = await fixture.AddProjectAsync("專案一", rd.Id);
        var second = await fixture.AddProjectAsync("專案二", rd.Id);
        var secret = await fixture.AddProjectAsync("業務機密", sales.Id);
        var aliceService = fixture.CreateProjectService(alice.Id);
        var bobService = fixture.CreateProjectService(bob.Id);

        Assert.Null(await aliceService.GetLastSelectedProjectIdAsync());

        await aliceService.SaveLastSelectedProjectAsync(second.Id);
        await bobService.SaveLastSelectedProjectAsync(first.Id);
        await aliceService.SaveLastSelectedProjectAsync(secret.Id);

        Assert.Equal(second.Id, await aliceService.GetLastSelectedProjectIdAsync());
        Assert.Equal(first.Id, await bobService.GetLastSelectedProjectIdAsync());
    }

    [Fact]
    public async Task LastSelectedProject_UnresolvedUser_ReadsNullAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rd = await fixture.AddTeamAsync("研發部");
        var project = await fixture.AddProjectAsync("專案一", rd.Id);
        var service = fixture.CreateProjectService(0, isAdmin: true);

        await service.SaveLastSelectedProjectAsync(project.Id);

        Assert.Null(await service.GetLastSelectedProjectIdAsync());
    }

    [Fact]
    public void ResolveProjectTeams_NotPrimaryMember_SameTeams_IsAllowed()
    {
        var access = new ProjectAccess(false, 7, [2], [5], []);

        var unchanged = access.ResolveProjectTeams(1, 1, [2, 3], projectId: 5, existingCollaboratorTeamIds: [3, 2]);
        var changed = access.ResolveProjectTeams(1, 1, [2], projectId: 5, existingCollaboratorTeamIds: [3, 2]);

        Assert.Null(unchanged.Error);
        Assert.Equal(ProjectAccess.ProjectTeamManageMessage, changed.Error);
        Assert.Null(new ProjectAccess(false, 7, [1], [5], [5]).ResolveProjectTeams(1, 1, [], projectId: 5, existingCollaboratorTeamIds: [3]).Error);
    }

    [Fact]
    public async Task UpdateAndDelete_InvisibleProject_ShouldActAsNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var rd = await fixture.AddTeamAsync("研發部");
        var project = await fixture.AddProjectAsync("研發的專案", rd.Id);
        var service = fixture.CreateProjectService(alice.Id);

        var updated = await service.UpdateAsync(new ProjectAdapterModel { Id = project.Id, Title = "改掉", Status = "進行中", PrimaryTeamId = rd.Id });
        var deleted = await service.DeleteAsync(project.Id);

        Assert.False(updated.Success);
        Assert.False(deleted.Success);
        Assert.Equal("研發的專案", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task SelectableTeams_PrimaryIsOwnForNonAdmin_CollaboratorIsAll()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        await fixture.AddTeamAsync("業務部", alice.Id);
        await fixture.AddTeamAsync("研發部");
        var service = fixture.CreateProjectService(alice.Id);

        Assert.Equal(["業務部"], (await service.GetSelectablePrimaryTeamsAsync()).Select(x => x.Name).ToList());
        Assert.Equal(2, (await service.GetSelectableCollaboratorTeamsAsync()).Count);
        Assert.Equal(2, (await fixture.CreateProjectService(0, isAdmin: true).GetSelectablePrimaryTeamsAsync()).Count);
    }

    [Fact]
    public async Task ProjectList_CategoryFilter_ShouldMatchAnyCategory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rd = await fixture.AddTeamAsync("研發部");
        await fixture.AddProjectAsync("成大", [".NET", "成大"], rd.Id);
        await fixture.AddProjectAsync("新創", ["Java", "新創"], rd.Id);

        var result = await fixture.CreateProjectService(0, isAdmin: true)
            .GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50, CategoryFilters = ["成大"] });

        Assert.Equal(["成大"], result.Result.Select(x => x.Title).ToList());
    }

    #endregion

    #region 待辦

    [Fact]
    public async Task Todos_ShouldOnlyListVisibleProjects_AndCannotMoveIntoOthers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var sales = await fixture.AddTeamAsync("業務部", alice.Id);
        var rd = await fixture.AddTeamAsync("研發部");
        var mine = await fixture.AddProjectAsync("我的專案", sales.Id);
        var others = await fixture.AddProjectAsync("別人的專案", rd.Id);
        var myTodo = await fixture.AddTodoAsync("我的待辦", mine.Id);
        await fixture.AddTodoAsync("別人的待辦", others.Id);
        var service = fixture.CreateTodoService(alice.Id);

        var list = await service.GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50 });
        Assert.Equal(["我的待辦"], list.Result.Select(x => x.Title).ToList());

        var model = await service.GetAsync(myTodo.Id);
        model.ProjectId = others.Id;
        var moved = await service.UpdateAsync(model);

        Assert.False(moved.Success);
        Assert.Equal(mine.Id, (await fixture.Context.Todo.AsNoTracking().SingleAsync(x => x.Id == myTodo.Id)).ProjectId);
    }

    #endregion

    #region 團隊

    [Fact]
    public async Task SyncMembers_ShouldAddAndRemove_AndChangeVisibility()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var rd = await fixture.AddTeamAsync("研發部", alice.Id);
        await fixture.AddProjectAsync("研發的專案", rd.Id);
        var teamService = fixture.CreateTeamService();

        await teamService.SyncMembersAsync(rd.Id, [bob.Id, 999]);

        Assert.Equal([bob.Id], await teamService.GetMemberIdsAsync(rd.Id));
        Assert.Empty(await fixture.CreateProjectService(alice.Id).GetSelectableAsync());
        Assert.Single(await fixture.CreateProjectService(bob.Id).GetSelectableAsync());
    }

    [Fact]
    public async Task BatchAddAndRemove_ShouldOnlyTouchSelectedUsers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var carol = await fixture.AddUserAsync("carol");
        var sales = await fixture.AddTeamAsync("業務部", carol.Id);
        var service = fixture.CreateMyUserService();

        // carol 已經在團隊裡，不會重複加。
        Assert.Equal(2, await service.AddUsersToTeamAsync([alice.Id, bob.Id, carol.Id], sales.Id));
        Assert.Equal(1, await service.RemoveUsersFromTeamAsync([bob.Id], sales.Id));

        var members = await fixture.Context.UserTeam.AsNoTracking()
            .Where(x => x.TeamId == sales.Id)
            .Select(x => x.MyUserId)
            .OrderBy(x => x)
            .ToListAsync();
        Assert.Equal([alice.Id, carol.Id], members);
    }

    [Fact]
    public async Task DeleteTeam_NonAdmin_StillPrimary_ShouldBeBlocked_CollaboratorOnly_ShouldPass()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        // alice 屬於業務部，看得到「業務的專案」，訊息才會列出它的名稱。
        var sales = await fixture.AddTeamAsync("業務部", alice.Id);
        var rd = await fixture.AddTeamAsync("研發部");
        await fixture.AddProjectAsync("業務的專案", sales.Id, rd.Id);
        var service = fixture.CreateTeamService(alice.Id);

        var check = await service.BeforeDeleteCheckAsync(new TeamAdapterModel { Id = sales.Id });
        var blocked = await service.DeleteAsync(sales.Id);
        var allowed = await service.DeleteAsync(rd.Id);

        Assert.False(check.Success);
        Assert.Contains("業務的專案", check.Message);
        Assert.False(blocked.Success);
        Assert.True(allowed.Success);
        Assert.Equal([sales.Id], await fixture.Context.ProjectTeam.Select(x => x.TeamId).ToListAsync());
    }

    [Fact]
    public async Task DeleteTeam_BlockedMessage_ShouldNotRevealInvisibleProjectNames()
    {
        // 0.4.114：看不到的專案只給數量，不列名稱。
        await using var fixture = await Fixture.CreateAsync();
        var outsider = await fixture.AddUserAsync("outsider");
        var sales = await fixture.AddTeamAsync("業務部");
        await fixture.AddProjectAsync("機密專案", sales.Id);

        var check = await fixture.CreateTeamService(outsider.Id).BeforeDeleteCheckAsync(new TeamAdapterModel { Id = sales.Id });

        Assert.False(check.Success);
        Assert.DoesNotContain("機密專案", check.Message);
        Assert.Contains("另有 1 個你看不到的專案", check.Message);
    }

    [Fact]
    public async Task UpdateProject_Rejected_ShouldNotLeakChangesIntoLaterSaves()
    {
        // 0.4.114：DbContext 是整條 Blazor 連線共用的。被拒絕的修改不能留在追蹤裡，
        // 否則下一次任何服務 SaveChanges 時會被一起寫進資料庫。
        await using var fixture = await Fixture.CreateAsync();
        var rd = await fixture.AddTeamAsync("研發部");
        var project = await fixture.AddProjectAsync("原本的標題", rd.Id);
        var service = fixture.CreateProjectService(0, isAdmin: true);

        var model = await service.GetAsync(project.Id);
        model.Title = "被拒絕的標題";
        model.PrimaryTeamId = 99999; // 不存在的團隊 → 驗證失敗
        var result = await service.UpdateAsync(model);
        await fixture.Context.SaveChangesAsync(); // 模擬同一條連線上別的服務存檔

        Assert.False(result.Success);
        Assert.Equal("原本的標題", (await fixture.Context.Project.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task AddProject_ShouldWriteBackNewId()
    {
        // 畫面靠它選取剛建立的專案（同名專案時用標題找會選錯）。
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("owner");
        var rd = await fixture.AddTeamAsync("研發部", owner.Id);
        var model = new ProjectAdapterModel { Title = "新專案", OwnerUserId = owner.Id, PrimaryTeamId = rd.Id };

        await fixture.CreateProjectService(0, isAdmin: true).AddAsync(model);

        Assert.Equal((await fixture.Context.Project.AsNoTracking().SingleAsync()).Id, model.Id);
    }

    [Fact]
    public async Task DeleteTeam_Admin_IsNeverBlocked()
    {
        // 管理者要做任何事都可以（0.4.104）：主責團隊照刪，專案就沒有主責。
        await using var fixture = await Fixture.CreateAsync();
        var sales = await fixture.AddTeamAsync("業務部");
        await fixture.AddProjectAsync("業務的專案", sales.Id);
        var service = fixture.CreateTeamService(isAdmin: true);

        var check = await service.BeforeDeleteCheckAsync(new TeamAdapterModel { Id = sales.Id });
        var result = await service.DeleteAsync(sales.Id);

        Assert.True(check.Success);
        Assert.True(result.Success);
        Assert.False(await fixture.Context.Team.AnyAsync(x => x.Id == sales.Id));
        Assert.False(await fixture.Context.ProjectTeam.AnyAsync());
    }

    #endregion

    #region 分類

    [Fact]
    public async Task CategoryTeams_AreReferenceOnly_SyncAndListed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rd = await fixture.AddTeamAsync("研發部");
        var sales = await fixture.AddTeamAsync("業務部");
        var category = new Category { Name = "成大" };
        fixture.Context.Category.Add(category);
        await fixture.Context.SaveChangesAsync();
        var service = fixture.CreateCategoryService();

        await service.SyncTeamsAsync(category.Id, [rd.Id, sales.Id, 999]);
        await service.SyncTeamsAsync(category.Id, [rd.Id]);

        var listed = (await service.GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50 })).Result.Single();
        Assert.Equal([rd.Id], listed.TeamIds);
        Assert.Equal(["研發部"], listed.TeamNames);
    }

    #endregion

    #region 舊資料轉換

    [Fact]
    public async Task Conversion_LegacyMembers_BecomeSameNamePrimaryTeam_AndEmptyGoesUnassigned()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        await fixture.AddTeamAsync("行銷案");
        var withMembers = await fixture.AddProjectAsync("行銷案");
        var empty = await fixture.AddProjectAsync("沒人的專案");
        await fixture.CreateLegacyMemberTableAsync((withMembers.Id, alice.Id), (withMembers.Id, bob.Id));

        await fixture.CreateConversionService().RunAsync();

        var links = await fixture.Context.ProjectTeam.AsNoTracking().Include(x => x.Team).ToListAsync();
        Assert.All(links, x => Assert.True(x.IsPrimary));
        // 撞到既有的「行銷案」團隊，改名加尾碼。
        var converted = links.Single(x => x.ProjectId == withMembers.Id).Team!;
        Assert.Equal("行銷案（專案）", converted.Name);
        var members = await fixture.Context.UserTeam.AsNoTracking()
            .Where(x => x.TeamId == converted.Id)
            .Select(x => x.MyUserId)
            .OrderBy(x => x)
            .ToListAsync();
        Assert.Equal([alice.Id, bob.Id], members);

        var unassigned = links.Single(x => x.ProjectId == empty.Id).Team!;
        Assert.Equal(TeamConversionService.UnassignedGroupName, unassigned.Name);
        Assert.False(await fixture.Context.UserTeam.AnyAsync(x => x.TeamId == unassigned.Id));

        // 升級前後看得到的人一樣：alice 看得到行銷案，看不到沒人的專案。
        Assert.Equal(["行銷案"], (await fixture.CreateProjectService(alice.Id).GetSelectableAsync()).Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task Conversion_0_4_101Data_GetsPrimaryFromExistingLink_OrUnassigned()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var teamA = await fixture.AddTeamAsync("A", alice.Id);
        var teamB = await fixture.AddTeamAsync("B");
        var linked = await fixture.AddProjectAsync("有掛團隊");
        var publicOne = await fixture.AddProjectAsync("0.4.101 的公開專案");
        // 0.4.101 的連結沒有主責欄位（全部 false）。
        fixture.Context.ProjectTeam.AddRange(
            new ProjectTeam { ProjectId = linked.Id, TeamId = teamB.Id },
            new ProjectTeam { ProjectId = linked.Id, TeamId = teamA.Id });
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateConversionService().RunAsync();
        await fixture.CreateConversionService().RunAsync();

        var links = await fixture.Context.ProjectTeam.AsNoTracking().Include(x => x.Team).ToListAsync();
        Assert.Equal(teamA.Id, links.Single(x => x.ProjectId == linked.Id && x.IsPrimary).TeamId);
        Assert.Equal(TeamConversionService.UnassignedGroupName, links.Single(x => x.ProjectId == publicOne.Id && x.IsPrimary).Team!.Name);
        Assert.Equal(3, links.Count);
        // 升級後沒有公開專案：alice 看不到原本公開的那個。
        Assert.Equal(["有掛團隊"], (await fixture.CreateProjectService(alice.Id).GetSelectableAsync()).Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task Conversion_ShouldDropLegacyTable_AndSecondRunDoesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var project = await fixture.AddProjectAsync("專案");
        await fixture.CreateLegacyMemberTableAsync((project.Id, alice.Id));

        await fixture.CreateConversionService().RunAsync();
        var teamsAfterFirst = await fixture.Context.Team.CountAsync();
        await fixture.CreateConversionService().RunAsync();

        Assert.False(await fixture.LegacyTableExistsAsync());
        Assert.Equal(teamsAfterFirst, await fixture.Context.Team.CountAsync());
        Assert.Equal(1, await fixture.Context.ProjectTeam.CountAsync());
    }

    [Fact]
    public void UniqueName_ShouldAppendSuffixUntilFree()
    {
        var used = new HashSet<string> { "A", "A（專案）" };

        Assert.Equal("B", TeamConversionService.UniqueName("B", used));
        Assert.Equal("A（專案 2）", TeamConversionService.UniqueName("A", used));
    }

    [Fact]
    public async Task Conversion_ShouldTakeUploaderFromFirstTranscriptionInLedger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync("alice");
        var bob = await fixture.AddUserAsync("bob");
        var meeting = new Meeting { Title = "舊會議" };
        fixture.Context.Meeting.Add(meeting);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.AiUsageLog.AddRange(
            new AiUsageLog { Feature = AiUsageFeature.Transcription, MeetingId = meeting.Id, UserId = bob.Id, OccurredAt = new DateTime(2026, 9, 20) },
            new AiUsageLog { Feature = AiUsageFeature.Transcription, MeetingId = meeting.Id, UserId = alice.Id, OccurredAt = new DateTime(2026, 9, 1) });
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        await fixture.CreateConversionService().RunAsync();

        // 最早那一筆才是當初上傳觸發的轉錄；之後的是別人按了重新轉錄。
        Assert.Equal(alice.Id, (await fixture.Context.Meeting.AsNoTracking().SingleAsync()).CreatedByUserId);
    }

    #endregion

    #region 待辦的來源會議（0.4.115）

    [Fact]
    public async Task TodoSourceMeeting_InAnotherTeamsProject_ShouldHideTitle_ButAdminSeesIt()
    {
        // 會議改歸屬到別的團隊的專案後，留在原專案的待辦不能再帶出那場會議的標題。
        await using var fixture = await Fixture.CreateAsync();
        var bob = await fixture.AddUserAsync("bob");
        var sales = await fixture.AddTeamAsync("業務部", bob.Id);
        var rd = await fixture.AddTeamAsync("研發部");
        var salesProject = await fixture.AddProjectAsync("業務專案", sales.Id);
        var rdProject = await fixture.AddProjectAsync("研發機密專案", rd.Id);
        var meeting = new Meeting { Title = "研發機密會議", ProjectId = rdProject.Id };
        fixture.Context.Meeting.Add(meeting);
        await fixture.Context.SaveChangesAsync();
        var todo = new Todo { Title = "跟進", ProjectId = salesProject.Id, MeetingId = meeting.Id, Status = "待辦", Priority = "中" };
        fixture.Context.Todo.Add(todo);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var bobService = fixture.CreateTodoService(bob.Id);
        var single = await bobService.GetAsync(todo.Id);
        var list = (await bobService.GetAsync(new DataRequest { CurrentPage = 1, PageSize = 50 })).Result;

        Assert.Equal("跟進", single.Title);
        Assert.True(string.IsNullOrEmpty(single.MeetingTitle));
        Assert.True(string.IsNullOrEmpty(Assert.Single(list).MeetingTitle));
        Assert.Equal("研發機密會議", (await fixture.CreateTodoService(0, isAdmin: true).GetAsync(todo.Id)).MeetingTitle);
    }

    [Fact]
    public async Task TodoUpdate_MovedToAnotherProject_ShouldDropSourceMeeting()
    {
        // 來源會議屬於原專案；搬到別的專案還留著，新專案的人就會看到他們看不到的會議標題。
        await using var fixture = await Fixture.CreateAsync();
        var team = await fixture.AddTeamAsync("研發部");
        var projectA = await fixture.AddProjectAsync("專案A", team.Id);
        var projectB = await fixture.AddProjectAsync("專案B", team.Id);
        var meeting = new Meeting { Title = "A 的會議", ProjectId = projectA.Id };
        fixture.Context.Meeting.Add(meeting);
        await fixture.Context.SaveChangesAsync();
        var todo = new Todo { Title = "抽出的待辦", ProjectId = projectA.Id, MeetingId = meeting.Id, Status = "待辦", Priority = "中" };
        fixture.Context.Todo.Add(todo);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        var service = fixture.CreateTodoService(0, isAdmin: true);

        var sameProject = await service.GetAsync(todo.Id);
        sameProject.Title = "改標題";
        Assert.True((await service.UpdateAsync(sameProject)).Success);
        Assert.Equal(meeting.Id, (await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id)).MeetingId);

        var moved = await service.GetAsync(todo.Id);
        moved.ProjectId = projectB.Id;
        Assert.True((await service.UpdateAsync(moved)).Success);
        Assert.Null((await fixture.Context.Todo.AsNoTracking().FirstAsync(x => x.Id == todo.Id)).MeetingId);
    }

    #endregion

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly IMapper mapper;
        private readonly IOptions<SystemSettings> systemSettings;
        private readonly string rootPath;

        private Fixture(SqliteConnection connection, BackendDBContext context, string rootPath)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();

            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = Path.Combine(rootPath, "projectfile");
            settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "aichat");
            systemSettings = Options.Create(settings);
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(
                new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new Fixture(connection, context, rootPath);
        }

        private ProjectAccessService Access(int userId, bool isAdmin)
            => isAdmin ? TestProjectAccess.Admin(Context, userId) : TestProjectAccess.User(Context, userId);

        public ProjectService CreateProjectService(int userId, bool isAdmin = false)
            => new(
                Context,
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                systemSettings,
                new AiChatStore(systemSettings, loggerFactory.CreateLogger<AiChatStore>()),
                Access(userId, isAdmin),
                TestAiServices.CreateCallLogService(Context),
                TestAuditLogs.CreateService(Context, TestAuditActor.Fixed(userId, $"user{userId}")));

        public TodoService CreateTodoService(int userId, bool isAdmin = false)
            => new(Context, mapper, loggerFactory.CreateLogger<TodoService>(), Access(userId, isAdmin), TestAuditLogs.CreateService(Context, TestAuditActor.Fixed(userId, $"user{userId}")),
                TestTodoChanges.CreateRecorder(Context, Access(userId, isAdmin), TestAuditActor.Fixed(userId, $"user{userId}")));

        public TeamService CreateTeamService(int userId = 0, bool isAdmin = false)
            => new(Context, mapper, loggerFactory.CreateLogger<TeamService>(), Access(userId, isAdmin), TestAuditLogs.CreateService(Context, TestAuditActor.Fixed(userId, $"user{userId}")));

        public CategoryService CreateCategoryService()
            => new(Context, mapper, loggerFactory.CreateLogger<CategoryService>(), TestAuditLogs.CreateService(Context));

        public MyUserService CreateMyUserService()
            => new(
                Context,
                mapper,
                loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context),
                TestAuditLogs.CreateService(Context),
                new CurrentUserService());

        public TeamConversionService CreateConversionService()
            => new(Context, loggerFactory.CreateLogger<TeamConversionService>());

        public async Task<MyUser> AddUserAsync(string account)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = true };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async Task<Team> AddTeamAsync(string name, params int[] memberIds)
        {
            var team = new Team { Name = name, IsEnabled = true };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.UserTeam.AddRange(memberIds.Select(id => new UserTeam { MyUserId = id, TeamId = team.Id }));
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        /// <summary>第一個團隊是主責，其餘是協作；沒給團隊＝升級前的舊資料（沒有任何連結）。</summary>
        public Task<Project> AddProjectAsync(string title, params int[] teamIds)
            => AddProjectAsync(title, [], teamIds);

        public async Task<Project> AddProjectAsync(string title, string[] categories, params int[] teamIds)
        {
            var project = new Project
            {
                Title = title,
                Status = "進行中",
                Categories = MeetingRecord.Business.Helpers.TagStringHelper.ToStored(categories),
                Teams = [.. teamIds.Select((id, index) => new ProjectTeam { TeamId = id, IsPrimary = index == 0 })],
            };
            Context.Project.Add(project);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return project;
        }

        public async Task<Todo> AddTodoAsync(string title, int projectId)
        {
            var todo = new Todo { Title = title, ProjectId = projectId, Status = "待辦", Priority = "中" };
            Context.Todo.Add(todo);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return todo;
        }

        /// <summary>0.4.99～0.4.100 的專案成員表，已不在 EF 模型裡，用原生 SQL 模擬升級前的資料庫。</summary>
        public async Task CreateLegacyMemberTableAsync(params (int ProjectId, int UserId)[] rows)
        {
            await Context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"ProjectMember\" (\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, \"ProjectId\" INTEGER NOT NULL, \"MyUserId\" INTEGER NOT NULL, \"Role\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL)");
            foreach (var (projectId, userId) in rows)
            {
                await Context.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"ProjectMember\" (\"ProjectId\", \"MyUserId\", \"Role\", \"CreatedAt\") VALUES ({projectId}, {userId}, 1, '2026-09-28')");
            }
        }

        public async Task<bool> LegacyTableExistsAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProjectMember'";
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // 測試用的暫存目錄清不掉不該讓測試失敗。
            }
        }
    }
}
