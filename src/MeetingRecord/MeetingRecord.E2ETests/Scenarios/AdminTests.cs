using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>帳號與權限（只限管理者）：使用者管理、角色管理。</summary>
public sealed class AdminTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "ADM-01")]
    [Trait("Title", "管理者新增使用者")]
    [Trait("Page", "使用者管理")]
    [Trait("Persona", "管理者")]
    public async Task Admin_ShouldCreateUser()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/myusers", "帳號");
        var account = Unique("e2e.new");

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入帳號").FillAsync(account);
        await page.GetByPlaceholder("新增時請輸入密碼，編輯時留白表示不變更").FillAsync("E2e-New-Pass-1");
        await page.GetByPlaceholder("請輸入名稱").FillAsync("新同事");
        await PickAsync(page, SelectByLabel(page, "角色"), "一般使用者");
        await ClickModalOkAsync(page);

        await Expect(Row(page, account)).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "ADM-05")]
    [Trait("Title", "使用者管理：修改團隊、批次加入團隊、停用再啟用；不能停用自己")]
    [Trait("Page", "使用者管理")]
    [Trait("Persona", "管理者")]
    public async Task Admin_EditTeams_BatchTeam_ToggleStatus_ButNotSelf()
    {
        // 1.1.10（使用者管理搬到 WASM）：ADM-01 只驗新增，補上修改、批次團隊、啟用切換，以及搬進伺服器的「不能停用自己」。
        var account = Unique("e2e.member");
        int userId;
        await using (var db = App.CreateDbContext())
        {
            var role = await db.RoleView.FirstAsync();
            var user = new MeetingRecord.AccessDatas.Models.MyUser
            {
                Account = account,
                Name = "批次測試",
                Status = true,
                RoleViewId = role.Id,
                Password = MeetingRecord.Business.Helpers.SecurePasswordHasher.HashPassword("E2e-Member-Pass-1"),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/myusers", "帳號");
        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync(account);
        await ToolbarButton(page, "搜尋").ClickAsync();
        await Expect(Row(page, account)).ToBeVisibleAsync();

        // 修改：加入團隊甲。
        await RowAction(Row(page, account), "修改").ClickAsync();
        await PickAsync(page, SelectByLabel(page, "團隊"), E2ESeedData.TeamA);
        // 多選下拉選完不會自己收起來（會蓋住「確定」）：點一下對話窗標題收起。
        await page.Locator(".ant-modal-wrap:visible .ant-modal-title").ClickAsync();
        await ClickModalOkAsync(page);
        await Expect(Notice(page, "修改成功")).ToBeVisibleAsync();

        // 批次：勾選這一列、加入團隊乙。
        await Row(page, account).Locator(".ant-checkbox-input").First.CheckAsync();
        await PickAsync(page, page.Locator(".my-user-batch .ant-select").First, E2ESeedData.TeamB);
        await ToolbarButton(page, "加入團隊").ClickAsync();
        await ConfirmAsync(page, "加入");
        await Expect(Notice(page, "已加入 1 人")).ToBeVisibleAsync();

        await using (var db = App.CreateDbContext())
        {
            var teams = await db.UserTeam.Where(x => x.MyUserId == userId).Select(x => x.Team!.Name).ToListAsync();
            Assert.Equal(new[] { E2ESeedData.TeamA, E2ESeedData.TeamB }.Order(StringComparer.Ordinal), teams.Order(StringComparer.Ordinal));
        }

        // 停用再啟用。
        await Row(page, account).GetByRole(AriaRole.Button, new() { Name = $"{account} 目前為啟用，點擊改為停用" }).ClickAsync();
        await ConfirmAsync(page, "停用");
        await Expect(Notice(page, $"已停用「{account}」")).ToBeVisibleAsync();
        await Row(page, account).GetByRole(AriaRole.Button, new() { Name = $"{account} 目前為停用，點擊改為啟用" }).ClickAsync();
        await ConfirmAsync(page, "啟用");
        await Expect(Notice(page, $"已啟用「{account}」")).ToBeVisibleAsync();

        // 不能停用自己：伺服器擋下並說明原因。
        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync(Seed.Admin.Account);
        await ToolbarButton(page, "搜尋").ClickAsync();
        await Row(page, Seed.Admin.Account).GetByRole(AriaRole.Button, new() { Name = $"{Seed.Admin.Account} 目前為啟用，點擊改為停用" }).ClickAsync();
        await ConfirmAsync(page, "停用");
        await Expect(Notice(page, "不能停用自己的帳號")).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "ADM-02")]
    [Trait("Title", "一鍵建立預設角色")]
    [Trait("Page", "角色管理")]
    [Trait("Persona", "管理者")]
    public async Task Admin_ShouldCreatePresetRoles()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/roleviews", "名稱");

        await page.GetByRole(AriaRole.Button, new() { Name = "建立預設角色" }).ClickAsync();
        await ConfirmAsync(page, "建立");

        await Expect(Notice(page, "預設角色")).ToBeVisibleAsync();
        await Expect(Row(page, "主管")).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "ADM-03")]
    [Trait("Title", "角色編修視窗顯示動作級權限矩陣")]
    [Trait("Page", "角色管理")]
    [Trait("Persona", "管理者")]
    public async Task Admin_RoleEditor_ShouldShowActionLevelPermissions()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/roleviews", "名稱");

        await RowAction(Row(page, "檢視者"), "修改").ClickAsync();

        var modal = page.Locator(".ant-modal-wrap:visible");
        await Expect(modal.GetByText("專案項目（全部）")).ToBeVisibleAsync();
        await Expect(modal.GetByText("檢視").First).ToBeVisibleAsync();
    }

    [E2ETheory]
    [InlineData("/myusers")]
    [InlineData("/roleviews")]
    [Trait("Scenario", "ADM-04")]
    [Trait("Title", "非管理者直接輸入網址被擋下")]
    [Trait("Page", "帳號與權限")]
    [Trait("Persona", "主責團隊成員")]
    public async Task NonAdmin_ShouldBeDenied(string path)
    {
        var page = await LoginAsAsync(Seed.Owner);

        await page.GotoAsync(path);

        await Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "你沒有權限存取此頁面" })).ToBeVisibleAsync();
    }
}
