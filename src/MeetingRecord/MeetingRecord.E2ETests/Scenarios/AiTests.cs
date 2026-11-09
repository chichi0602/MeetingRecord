using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// AI 功能（E2E 環境用假 AI，不會呼叫付費 API；仍經 AiCallGateway，所以用量帳本也一起被測到）。
/// </summary>
public sealed class AiTests(E2EAppFixture app) : E2ETestBase(app)
{
    private const string Marker = "E2E 假 AI 回應";

    [E2EFact]
    [Trait("Scenario", "AI-01")]
    [Trait("Title", "專案 AI 問答：串流顯示回答並記入用量帳本")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ProjectAiChat_ShouldStreamAnswer()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        await PickAsync(page, page.Locator(".project-view-project-select"), E2ESeedData.SharedProject);
        await using var db = App.CreateDbContext();
        var before = await db.AiUsageLog.CountAsync();

        await page.Locator("button").Filter(new() { HasText = "AI 問答" }).First.ClickAsync();
        var question = page.GetByPlaceholder("輸入問題後按 Enter 送出", new() { Exact = false });
        await question.FillAsync("這個專案下週要做什麼？");
        await question.DispatchEventAsync("change");
        await ToolbarButton(page, "送出").ClickAsync();

        await Expect(page.Locator(".ai-chat-row-assistant").Last).ToContainTextAsync(Marker);
        // 帳本在回答串流結束後才寫，等一下。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await db.AiUsageLog.CountAsync() <= before)
        {
            Assert.True(DateTime.UtcNow < deadline, "AI 問答要記進用量帳本");
            await Task.Delay(300);
        }
    }

    [E2EFact]
    [Trait("Scenario", "AI-02")]
    [Trait("Title", "使用說明「問 AI 助理」回答")]
    [Trait("Page", "使用說明")]
    [Trait("Persona", "主責團隊成員")]
    public async Task HelpAssistant_ShouldAnswer()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/help", "問 AI 助理");

        var question = page.GetByPlaceholder("輸入操作上的問題", new() { Exact = false });
        await question.FillAsync("怎麼把會議歸屬到專案？");
        // 文字框在 change（離開欄位）時才繫結，「發問」按鈕才會啟用。
        await question.DispatchEventAsync("change");
        await ToolbarButton(page, "發問").ClickAsync();

        await Expect(page.Locator(".help-view-assistant-answer")).ToContainTextAsync(Marker);
    }

    [E2EFact]
    [Trait("Scenario", "AI-03")]
    [Trait("Title", "從會議紀錄抽出待辦（開窗前費用確認）並加入")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ExtractTodos_FromMinutes()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        await PickAsync(page, page.Locator(".project-view-project-select"), E2ESeedData.SharedProject);

        await ClearJobPanelAsync(page);
        await RowAction(Row(page, E2ESeedData.MinutesMeeting), "抽出待辦").ClickAsync();
        // 費用確認在開窗之前（6.3）：確認框出現時還沒有抽取視窗。
        await Expect(page.Locator(".ant-modal-confirm").Filter(new() { HasText = "會產生費用" })).ToBeVisibleAsync();
        await ConfirmAsync(page, "開始抽取");

        var modal = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "從會議紀錄抽出待辦" });
        await Expect(modal.GetByPlaceholder("待辦標題（必填）").First).ToHaveValueAsync("E2E 假待辦：完成介面設計");
        await modal.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("加入 1 條待辦") }).ClickAsync();

        await using var db = App.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await db.Todo.AnyAsync(x => x.Title == "E2E 假待辦：完成介面設計" && x.ProjectId == Seed.SharedProjectId))
        {
            Assert.True(DateTime.UtcNow < deadline, "抽出的待辦沒有寫進資料庫");
            await Task.Delay(300);
        }
    }
}
