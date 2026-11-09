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
