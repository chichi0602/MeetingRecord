using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.E2ETests.Infrastructure;

/// <summary>端對端測試的人物（帳號）。</summary>
public sealed record E2EPersona(string Key, string Account, string Name, string Password)
{
    public int Id { get; set; }
}

/// <summary>種子資料：6 種人物（加上強制改密碼、已停用、鎖定測試三個特殊帳號）、兩個團隊、三個專案、會議、待辦。</summary>
public sealed class E2ESeedData
{
    public const string Password = "E2e-User-Pass-2026";

    /// <summary>管理者（啟動時自動建立的 support）。</summary>
    public E2EPersona Admin { get; } = new("admin", "support", "support", E2EAppFixture.AdminPassword);

    /// <summary>甲隊一般使用者，「E2E 共同專案」的負責人。</summary>
    public E2EPersona Owner { get; } = new("owner", "e2e.owner", "王主責", Password);

    /// <summary>乙隊一般使用者：「E2E 共同專案」的協作團隊成員、「E2E 乙隊專案」的負責人。</summary>
    public E2EPersona Collaborator { get; } = new("collab", "e2e.collab", "李協作", Password);

    /// <summary>甲隊、檢視者角色（只能看）。</summary>
    public E2EPersona Viewer { get; } = new("viewer", "e2e.viewer", "陳檢視", Password);

    /// <summary>乙隊、甲方角色＋甲方帳號（IsExternal）。</summary>
    public E2EPersona Client { get; } = new("client", "e2e.client", "林甲方", Password);

    /// <summary>沒有任何團隊的一般使用者：看不到任何專案。</summary>
    public E2EPersona NoTeam { get; } = new("noteam", "e2e.noteam", "趙無團隊", Password);

    public E2EPersona MustChangePassword { get; } = new("newbie", "e2e.newbie", "周新人", Password);

    public E2EPersona Disabled { get; } = new("disabled", "e2e.disabled", "吳停用", Password);

    /// <summary>專門給「密碼打錯」測試用，避免把別的人物鎖住（錯 5 次鎖 15 分鐘）。</summary>
    public E2EPersona LockTarget { get; } = new("locktest", "e2e.locktest", "鄭鎖定", Password);

    public const string TeamA = "E2E甲隊";
    public const string TeamB = "E2E乙隊";
    public const string SharedProject = "E2E 共同專案";
    public const string TeamBProject = "E2E 乙隊專案";
    public const string ArchivedProject = "E2E 已完成專案";
    public const string SharedMeeting = "E2E 共同專案週會";
    public const string TeamBMeeting = "E2E 乙隊會議";
    public const string SharedTodo = "E2E 共同專案待辦";
    public const string TeamBTodo = "E2E 乙隊待辦";
    public const string ClientTodo = "E2E 甲方建立的待辦";
    public const string MinutesMeeting = "E2E 已有會議紀錄的會議";
    public const string PromptTemplate = "E2E 標準提示詞";

    public int SharedProjectId { get; set; }
    public int TeamBProjectId { get; set; }
    public int ArchivedProjectId { get; set; }

    public IEnumerable<E2EPersona> All => [Admin, Owner, Collaborator, Viewer, Client, NoTeam, MustChangePassword, Disabled, LockTarget];
}

public static class E2ESeeder
{
    public static async Task<E2ESeedData> SeedAsync(Func<BackendDBContext> createContext)
    {
        var seed = new E2ESeedData();
        await using var db = createContext();
        var rbac = new RbacWriteService(db);

        seed.Admin.Id = (await db.MyUser.AsNoTracking().SingleAsync(x => x.Account == seed.Admin.Account)).Id;

        // 角色照「建立預設角色」的定義建立（一般使用者在網站啟動時已經建好）。
        var roles = new Dictionary<string, int>();
        foreach (var preset in RolePresets.All.Where(p => p.Name is MagicObjectHelper.預設角色 or "檢視者" or RolePresets.甲方角色名稱))
        {
            var role = await db.RoleView.FirstOrDefaultAsync(x => x.Name == preset.Name);
            if (role is null)
            {
                role = new RoleView { Name = preset.Name, TabViewJson = "[]" };
                db.RoleView.Add(role);
                await db.SaveChangesAsync();
            }

            await rbac.SyncRolePermissionsAsync(role.Id, preset.PermissionKeys);
            roles[preset.Name] = role.Id;
        }

        var teamA = new Team { Name = E2ESeedData.TeamA, IsEnabled = true };
        var teamB = new Team { Name = E2ESeedData.TeamB, IsEnabled = true };
        db.Team.AddRange(teamA, teamB);
        await db.SaveChangesAsync();

        async Task AddUserAsync(E2EPersona persona, string roleName, Team? team, Action<MyUser>? configure = null)
        {
            var user = new MyUser
            {
                Account = persona.Account,
                Name = persona.Name,
                Password = SecurePasswordHasher.HashPassword(persona.Password),
                Status = true,
                IsAdmin = false,
                RoleViewId = roles[roleName],
                Email = $"{persona.Account}@example.invalid",
            };
            configure?.Invoke(user);
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await rbac.SyncUserRolesAsync(user.Id, [roles[roleName]]);
            if (team is not null)
            {
                db.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = team.Id });
                await db.SaveChangesAsync();
            }

            persona.Id = user.Id;
        }

        await AddUserAsync(seed.Owner, MagicObjectHelper.預設角色, teamA);
        await AddUserAsync(seed.Collaborator, MagicObjectHelper.預設角色, teamB);
        await AddUserAsync(seed.Viewer, "檢視者", teamA);
        await AddUserAsync(seed.Client, RolePresets.甲方角色名稱, teamB, u => u.IsExternal = true);
        await AddUserAsync(seed.NoTeam, MagicObjectHelper.預設角色, null);
        await AddUserAsync(seed.MustChangePassword, MagicObjectHelper.預設角色, teamA, u => u.MustChangePassword = true);
        await AddUserAsync(seed.Disabled, MagicObjectHelper.預設角色, teamA, u => u.Status = false);
        await AddUserAsync(seed.LockTarget, MagicObjectHelper.預設角色, teamA);

        Project NewProject(string title, string status, E2EPersona owner, Team primary, Team? collaborator)
        {
            var project = new Project
            {
                Title = title,
                Status = status,
                StartDate = DateTime.Today.AddDays(-30),
                EndDate = DateTime.Today.AddDays(60),
                Owner = owner.Name,
                OwnerUserId = owner.Id,
            };
            project.Teams.Add(new ProjectTeam { Team = primary, IsPrimary = true });
            if (collaborator is not null)
            {
                project.Teams.Add(new ProjectTeam { Team = collaborator, IsPrimary = false });
            }

            return project;
        }

        var shared = NewProject(E2ESeedData.SharedProject, "進行中", seed.Owner, teamA, teamB);
        var teamBProject = NewProject(E2ESeedData.TeamBProject, "進行中", seed.Collaborator, teamB, null);
        var archived = NewProject(E2ESeedData.ArchivedProject, "已完成", seed.Owner, teamA, null);
        db.Project.AddRange(shared, teamBProject, archived);
        await db.SaveChangesAsync();
        seed.SharedProjectId = shared.Id;
        seed.TeamBProjectId = teamBProject.Id;
        seed.ArchivedProjectId = archived.Id;

        db.Meeting.AddRange(
            new Meeting { Title = E2ESeedData.SharedMeeting, ProjectId = shared.Id, CreatedByUserId = seed.Owner.Id, MeetingDate = DateTime.Today },
            new Meeting { Title = E2ESeedData.TeamBMeeting, ProjectId = teamBProject.Id, CreatedByUserId = seed.Collaborator.Id, MeetingDate = DateTime.Today });

        // 已經有會議紀錄的會議：專案頁「抽出待辦」與會議紀錄 PDF 用（不必先跑一趟生成）。
        db.Meeting.Add(new Meeting
        {
            Title = E2ESeedData.MinutesMeeting,
            ProjectId = shared.Id,
            CreatedByUserId = seed.Owner.Id,
            MeetingDate = DateTime.Today,
            DraftStatus = MeetingRecord.Share.Enums.DraftStatus.Completed,
            DraftContent = "## 決議\n\n- 王主責下週五前完成介面設計。",
            DraftPromptTemplateName = E2ESeedData.PromptTemplate,
            DraftCompletedAt = DateTime.Now,
        });

        db.PromptTemplate.Add(new PromptTemplate
        {
            Name = E2ESeedData.PromptTemplate,
            Content = "請依逐字稿整理會議紀錄，列出決議與待辦。",
            IsEnabled = true,
        });

        db.Todo.AddRange(
            new Todo { Title = E2ESeedData.SharedTodo, ProjectId = shared.Id, Owner = seed.Owner.Name, Status = "待辦", Priority = "中", DueDate = DateTime.Today.AddDays(7), CreatedByUserId = seed.Owner.Id },
            new Todo { Title = E2ESeedData.TeamBTodo, ProjectId = teamBProject.Id, Owner = seed.Collaborator.Name, Status = "待辦", Priority = "中", DueDate = DateTime.Today.AddDays(7), CreatedByUserId = seed.Collaborator.Id },
            new Todo { Title = E2ESeedData.ClientTodo, ProjectId = shared.Id, Owner = seed.Client.Name, Status = "待辦", Priority = "低", DueDate = DateTime.Today.AddDays(14), CreatedByUserId = seed.Client.Id });
        await db.SaveChangesAsync();

        return seed;
    }
}
