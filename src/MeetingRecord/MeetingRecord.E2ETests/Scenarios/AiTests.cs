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

    /// <summary>假 AI 的慢速標記（同 <c>E2EFakeAi.SlowMarker</c>；E2ETests 不參考 Web）。</summary>
    private const string SlowMarker = "E2E-慢速";

    [E2EFact]
    [Trait("Scenario", "AI-05")]
    [Trait("Title", "使用說明 AI 助理按「停止」：伺服器停止呼叫 AI，帳本記成取消、稽核記停止")]
    [Trait("Page", "使用說明")]
    [Trait("Persona", "主責團隊成員")]
    public async Task HelpAssistant_Stop_ShouldCancelOnTheServer()
    {
        // 1.1.30 使用說明頁搬到 WASM：「停止」＝中斷請求，伺服器要跟著停（不再繼續花費），稽核由伺服器寫。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/help", "問 AI 助理");
        await using var db = App.CreateDbContext();
        var ownerId = await db.MyUser.Where(x => x.Account == Seed.Owner.Account).Select(x => x.Id).SingleAsync();
        var cancelledBefore = await db.AiUsageLog.CountAsync(x => x.UserId == ownerId && x.Outcome == MeetingRecord.Share.Enums.AiUsageOutcome.Cancelled);
        var stoppedBefore = await db.AuditLog.CountAsync(x => x.ActorUserId == ownerId && x.Action == "Help.AiAsk" && x.Detail!.Contains("outcome=停止"));

        var question = page.GetByPlaceholder("輸入操作上的問題", new() { Exact = false });
        await question.FillAsync($"{SlowMarker} 請慢慢回答");
        await question.DispatchEventAsync("change");
        await ToolbarButton(page, "發問").ClickAsync();

        // 串流開始（看到第一段文字）就按停止。
        await Expect(page.Locator(".help-view-assistant-streaming")).ToBeVisibleAsync();
        await ToolbarButton(page, "停止").ClickAsync();
        await Expect(page.Locator(".help-view-assistant-answer").Last).ToContainTextAsync("（已停止）");
        await Expect(ToolbarButton(page, "發問")).ToBeVisibleAsync();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await db.AiUsageLog.CountAsync(x => x.UserId == ownerId && x.Outcome == MeetingRecord.Share.Enums.AiUsageOutcome.Cancelled) <= cancelledBefore
               || await db.AuditLog.CountAsync(x => x.ActorUserId == ownerId && x.Action == "Help.AiAsk" && x.Detail!.Contains("outcome=停止")) <= stoppedBefore)
        {
            Assert.True(DateTime.UtcNow < deadline, "按停止後，伺服器要把這次呼叫記成取消、稽核記「停止」");
            await Task.Delay(300);
        }
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

    [E2EFact]
    [Trait("Scenario", "AI-04")]
    [Trait("Title", "會議 AI 問答（WASM 頁）：串流顯示回答並記入用量帳本")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task MeetingAiChat_OnWasmPage_ShouldStreamAnswer()
    {
        // 1.1.23 會議頁搬到 WASM：問答視窗改走 AiChatApi（打 API、邊收邊讀 NDJSON 串流），同 AI-01 的驗法。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        await using var db = App.CreateDbContext();
        var before = await db.AiUsageLog.CountAsync();

        // 全套跑時其他情境新增的會議會把這筆種子資料擠出第一頁（清單依更新時間排序），先用標題搜尋。
        await page.GetByPlaceholder("請輸入搜尋關鍵字").FillAsync(E2ESeedData.MinutesMeeting);
        await page.GetByPlaceholder("請輸入搜尋關鍵字").PressAsync("Enter");
        await RowAction(Row(page, E2ESeedData.MinutesMeeting), "AI 問答").ClickAsync();
        var question = page.GetByPlaceholder("輸入問題後按 Enter 送出", new() { Exact = false });
        await question.FillAsync("這場會議決定了什麼？");
        await question.DispatchEventAsync("change");
        await ToolbarButton(page, "送出").ClickAsync();

        await Expect(page.Locator(".ai-chat-row-assistant").Last).ToContainTextAsync(Marker);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await db.AiUsageLog.CountAsync() <= before)
        {
            Assert.True(DateTime.UtcNow < deadline, "AI 問答要記進用量帳本");
            await Task.Delay(300);
        }
    }
}
