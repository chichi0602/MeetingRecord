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
