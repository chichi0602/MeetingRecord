using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>資料定義：分類、團隊、提示詞。</summary>
public sealed class MasterDataTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "MD-01")]
    [Trait("Title", "分類新增、停用（二次確認）、刪除")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "管理者")]
    public async Task Category_CreateDisableDelete()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/categories", "名稱");
        var name = Unique("E2E分類");

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入分類名稱").FillAsync(name);
        await ClickModalOkAsync(page);
        await Expect(Row(page, name)).ToBeVisibleAsync();

        await Row(page, name).GetByRole(AriaRole.Button, new() { Name = $"{name} 目前為啟用，點擊改為停用" }).ClickAsync();
        await ConfirmAsync(page, "停用");
        await Expect(Notice(page, $"已停用「{name}」")).ToBeVisibleAsync();
        await Expect(Row(page, name).GetByText("停用", new() { Exact = true })).ToBeVisibleAsync();

        await RowAction(Row(page, name), "刪除").ClickAsync();
        await ConfirmAsync(page, "刪除");
        await Expect(Row(page, name)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MD-02")]
    [Trait("Title", "分類多選刪除")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "管理者")]
    public async Task Category_BatchDelete()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/categories", "名稱");
        var prefix = Unique("E2E批次");
        foreach (var suffix in new[] { "甲", "乙" })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
            await page.GetByPlaceholder("請輸入分類名稱").FillAsync($"{prefix}{suffix}");
            await ClickModalOkAsync(page);
            await Expect(Row(page, $"{prefix}{suffix}")).ToBeVisibleAsync();
        }

        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync(prefix);
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋" }).ClickAsync();
        await Expect(Row(page, prefix)).ToHaveCountAsync(2);
        foreach (var row in await Row(page, prefix).AllAsync())
        {
            await row.Locator(".ant-checkbox-input").First.CheckAsync();
        }

        await page.GetByRole(AriaRole.Region, new() { Name = "批次動作" }).GetByRole(AriaRole.Button, new() { Name = "刪除所選" }).ClickAsync();
        await ConfirmAsync(page, "刪除");

        await Expect(Row(page, prefix)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MD-03")]
    [Trait("Title", "團隊新增")]
    [Trait("Page", "團隊清單")]
    [Trait("Persona", "管理者")]
    public async Task Team_Create()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/teams", "成員數");
        var name = Unique("E2E團隊");

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入團隊名稱").FillAsync(name);
        await ClickModalOkAsync(page);

        await Expect(Row(page, name)).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "MD-07")]
    [Trait("Title", "團隊修改成員、刪除（刪除前先檢查再確認）")]
    [Trait("Page", "團隊清單")]
    [Trait("Persona", "管理者")]
    public async Task Team_EditMembersAndDelete()
    {
        // 1.1.8（團隊清單搬到 WASM）：以前只有 MD-03 一個團隊情境，補上修改成員與刪除。
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/teams", "成員數");
        var name = Unique("E2E成員團隊");

        await ToolbarButton(page, "新增").ClickAsync();
        await page.GetByPlaceholder("請輸入團隊名稱").FillAsync(name);
        await ClickModalOkAsync(page);
        await Expect(Row(page, name)).ToBeVisibleAsync();

        await RowAction(Row(page, name), "修改").ClickAsync();
        await PickAsync(page, SelectByLabel(page, "成員"), $"（{Seed.Viewer.Account}）");
        // 多選下拉選完不會自己收起來（會蓋住「確定」）：點一下對話窗標題收起。
        await page.Locator(".ant-modal-wrap:visible .ant-modal-title").ClickAsync();
        await Expect(page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden)")).ToHaveCountAsync(0);
        await ClickModalOkAsync(page);
        await Expect(Notice(page, "修改成功")).ToBeVisibleAsync();

        await using (var db = App.CreateDbContext())
        {
            var team = await db.Team.SingleAsync(x => x.Name == name);
            Assert.Equal([Seed.Viewer.Id], await db.UserTeam.Where(x => x.TeamId == team.Id).Select(x => x.MyUserId).ToListAsync());
        }

        await RowAction(Row(page, name), "刪除").ClickAsync();
        await ConfirmAsync(page, "刪除");
        await Expect(Row(page, name)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MD-04")]
    [Trait("Title", "提示詞一鍵建立內建範本")]
    [Trait("Page", "提示詞清單")]
    [Trait("Persona", "管理者")]
    public async Task PromptTemplate_CreateBuiltInPresets()
    {
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/prompttemplates", "內容預覽");

        await page.GetByRole(AriaRole.Button, new() { Name = "建立內建範本" }).ClickAsync();

        await Expect(Notice(page, "內建範本")).ToBeVisibleAsync();
        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync("標準會議紀錄");
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋" }).ClickAsync();
        await Expect(Row(page, "標準會議紀錄")).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "MD-09")]
    [Trait("Title", "儲存被伺服器擋下時（名稱重複），表單視窗留著、填的內容還在")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "管理者")]
    public async Task Category_DuplicateName_ShouldKeepTheForm()
    {
        // 1.1.19：AntDesign 按「確定」時會先關窗再呼叫存檔；WASM 頁以前雙向繫結，被擋下時視窗已經關了、輸入跟著不見。
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/categories", "名稱");
        var name = Unique("E2E重複分類");
        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入分類名稱").FillAsync(name);
        await ClickModalOkAsync(page);
        await Expect(Row(page, name)).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入分類名稱").FillAsync(name.ToUpperInvariant());
        await ClickModalOkAsync(page);

        await Expect(Notice(page, "分類名稱已存在，無法新增。")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(800);
        var input = page.Locator(".ant-modal-wrap:visible").GetByPlaceholder("請輸入分類名稱");
        await Expect(input).ToBeVisibleAsync();
        await Expect(input).ToHaveValueAsync(name.ToUpperInvariant());
    }

    [E2EFact]
    [Trait("Scenario", "MD-08")]
    [Trait("Title", "提示詞新增單一內建範本（重複會擋）、修改時選分類、未知變數只提醒不阻擋")]
    [Trait("Page", "提示詞清單")]
    [Trait("Persona", "管理者")]
    public async Task PromptTemplate_AddSinglePreset_EditCategoryAndUnknownVariable()
    {
        // 1.1.9（提示詞清單搬到 WASM）：MD-04 只驗「建立全部內建範本」，補上單一範本、分類與變數提醒（PromptVariableHelper 在 WASM 執行）。
        var preset = MeetingRecord.Business.Helpers.PromptTemplatePresets.All[^1].Name;
        var category = Unique("E2E提示詞分類");
        await using (var db = App.CreateDbContext())
        {
            await db.PromptTemplate.Where(x => x.Name == preset).ExecuteDeleteAsync();
            db.Category.Add(new MeetingRecord.AccessDatas.Models.Category { Name = category, IsEnabled = true });
            await db.SaveChangesAsync();
        }

        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/prompttemplates", "內容預覽");

        var presetSelect = page.Locator(".prompt-template-view-toolbar .ant-select").First;
        await PickAsync(page, presetSelect, preset);
        await ToolbarButton(page, "新增此範本").ClickAsync();
        await Expect(Notice(page, $"已新增「{preset}」")).ToBeVisibleAsync();

        await ToolbarButton(page, "新增此範本").ClickAsync();
        await Expect(Notice(page, $"「{preset}」已存在，未新增")).ToBeVisibleAsync();

        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync(preset);
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋" }).ClickAsync();
        await RowAction(Row(page, preset), "修改").ClickAsync();
        await PickAsync(page, SelectByLabel(page, "分類"), category);
        // 多選下拉選完不會自己收起來（會蓋住欄位與「確定」）：點一下對話窗標題收起。
        await page.Locator(".ant-modal-wrap:visible .ant-modal-title").ClickAsync();
        var content = page.GetByPlaceholder("請輸入產生會議紀錄時要送給模型的指令內容");
        await content.FillAsync(await content.InputValueAsync() + "\n{{foo}}");
        await ClickModalOkAsync(page);

        await Expect(Notice(page, "提示詞變數提醒")).ToBeVisibleAsync();
        await Expect(Notice(page, "修改成功")).ToBeVisibleAsync();
        await using (var db = App.CreateDbContext())
        {
            var saved = await db.PromptTemplate.SingleAsync(x => x.Name == preset);
            Assert.Contains("{{foo}}", saved.Content);
            Assert.Equal($"\n{category}\n", saved.Categories);
        }
    }

    [E2EFact]
    [Trait("Scenario", "MD-06")]
    [Trait("Title", "按 Enter 送出後立刻再按「確定」只新增一筆、不會跳名稱已存在")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "管理者")]
    public async Task Category_EnterThenOk_ShouldCreateOnlyOne()
    {
        // 1.0.62 修正：分類表單以前完全沒有重複送出防護，第二次靠名稱不可重複擋下、跳「名稱已存在」。
        var page = await LoginAsAsync(Seed.Admin);
        await GotoAsync(page, "/categories", "名稱");
        var name = Unique("E2E連按分類");

        await ToolbarButton(page, "新增").ClickAsync();
        var nameInput = page.GetByPlaceholder("請輸入分類名稱");
        await nameInput.FillAsync(name);
        await nameInput.PressAsync("Enter");
        await page.Locator(".ant-modal-wrap:visible .ant-modal-footer .ant-btn-primary").Last.ClickAsync(new() { NoWaitAfter = true, Timeout = 2000 }).ContinueWith(_ => { });
        await Expect(Row(page, name)).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1500);

        await Expect(page.Locator(".ant-notification-notice").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("已存在|重複") })).ToHaveCountAsync(0);
        await using var db = App.CreateDbContext();
        Assert.Equal(1, await db.Category.CountAsync(x => x.Name == name));
    }

    [E2EFact]
    [Trait("Scenario", "MD-05")]
    [Trait("Title", "一般使用者看不到資料定義頁")]
    [Trait("Page", "分類清單")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Category_NormalUser_ShouldBeDenied()
    {
        var page = await LoginAsAsync(Seed.Owner);

        await page.GotoAsync("/categories");

        await Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "你沒有權限存取此頁面" })).ToBeVisibleAsync();
    }
}
