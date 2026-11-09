using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>待辦事項頁：可見範圍、新增、改期限要寫原因、勾完成、子項目、甲方規則、多選刪除。</summary>
public sealed class TodoTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2ETheory]
    [InlineData("主責團隊成員")]
    [InlineData("協作團隊成員")]
    [InlineData("無團隊使用者")]
    [Trait("Scenario", "TDO-01")]
    [Trait("Title", "待辦清單只列出看得到的專案的待辦")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "依人物")]
    public async Task TodoList_ShouldFollowProjectVisibility(string persona)
    {
        var (user, sees, hides) = persona switch
        {
            "主責團隊成員" => (Seed.Owner, new[] { E2ESeedData.SharedTodo }, new[] { E2ESeedData.TeamBTodo }),
            "協作團隊成員" => (Seed.Collaborator, new[] { E2ESeedData.SharedTodo, E2ESeedData.TeamBTodo }, Array.Empty<string>()),
            _ => (Seed.NoTeam, Array.Empty<string>(), new[] { E2ESeedData.SharedTodo, E2ESeedData.TeamBTodo }),
        };
        var page = await LoginAsAsync(user);
        await GotoAsync(page, "/todos", "待辦事項");

        foreach (var title in sees)
        {
            await SearchAsync(page, title);
            await Expect(Row(page, title)).ToBeVisibleAsync();
        }

        foreach (var title in hides)
        {
            await SearchAsync(page, title);
            await Expect(Row(page, title)).ToHaveCountAsync(0);
        }
    }

    [E2EFact]
    [Trait("Scenario", "TDO-02")]
    [Trait("Title", "新增待辦")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_Create()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var title = await CreateTodoAsync(page, "E2E 新待辦");

        await Expect(Row(page, title)).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "TDO-03")]
    [Trait("Title", "改期限一定要寫原因")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_ChangeDueDate_RequiresReason()
    {
        var page = await LoginAsAsync(Seed.Owner);
        // 第一次設定期限不算「改期限」，所以新增時就給期限，之後再改。
        var title = await CreateTodoAsync(page, "E2E 改期限", dueDate: DateTime.Today.AddDays(7));

        await RowAction(Row(page, title), "修改").ClickAsync();
        await SetDueDateAsync(page, DateTime.Today.AddDays(30));

        var reason = page.GetByPlaceholder("例如：甲方要求延後驗收、等待對方提供資料…（會通知專案成員）");
        await Expect(reason).ToBeVisibleAsync();
        await ClickModalOkAsync(page);
        await Expect(page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "改期限原因" })).ToBeVisibleAsync();

        await reason.FillAsync("等待甲方提供資料");
        await ClickModalOkAsync(page);
        await Expect(Notice(page, "修改成功")).ToBeVisibleAsync();

        await using var db = App.CreateDbContext();
        var todo = await db.Todo.AsNoTracking().SingleAsync(x => x.Title == title);
        Assert.Equal(DateTime.Today.AddDays(30), todo.DueDate);
        Assert.Contains(await db.TodoChange.AsNoTracking().Where(x => x.TodoId == todo.Id).Select(x => x.Note).ToListAsync(), n => n != null && n.Contains("等待甲方提供資料"));
    }

    [E2EFact]
    [Trait("Scenario", "TDO-04")]
    [Trait("Title", "勾選完成")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_MarkCompleted()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var title = await CreateTodoAsync(page, "E2E 勾完成");

        // 欄位：勾選（多選）、完成、待辦事項……；第二欄是「完成」。
        await Row(page, title).Locator("td:nth-child(2) .ant-checkbox").ClickAsync();

        await using var db = App.CreateDbContext();
        await WaitUntilAsync(async () => (await db.Todo.AsNoTracking().SingleAsync(x => x.Title == title)).Status == "已完成");
    }

    [E2EFact]
    [Trait("Scenario", "TDO-05")]
    [Trait("Title", "新增子項目，主待辦顯示完成數")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_AddSubItem()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var title = await CreateTodoAsync(page, "E2E 主待辦");

        await RowAction(Row(page, title), "新增子項目").ClickAsync();
        await page.GetByPlaceholder("請輸入待辦標題").FillAsync($"{title} 的子項目");
        await ClickModalOkAsync(page);

        await Expect(Row(page, title).First).ToContainTextAsync("子項目 0/1");
    }

    [E2EFact]
    [Trait("Scenario", "TDO-06")]
    [Trait("Title", "甲方只能刪自己建立的待辦")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "甲方")]
    public async Task Client_CanDeleteOnlyOwnTodo()
    {
        var page = await LoginAsAsync(Seed.Client);
        var own = await CreateTodoAsync(page, "E2E 甲方自己的");

        await Expect(RowAction(Row(page, own), "刪除")).ToHaveCountAsync(1);
        await SearchAsync(page, E2ESeedData.SharedTodo);
        await Expect(Row(page, E2ESeedData.SharedTodo)).ToBeVisibleAsync();
        await Expect(RowAction(Row(page, E2ESeedData.SharedTodo), "刪除")).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "TDO-07")]
    [Trait("Title", "多選刪除")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員（專案負責人）")]
    public async Task Todo_BatchDelete()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var prefix = Unique("E2E批刪");
        await CreateTodoAsync(page, prefix, exactTitle: $"{prefix}甲");
        await CreateTodoAsync(page, prefix, exactTitle: $"{prefix}乙");

        await SearchAsync(page, prefix);
        await Expect(Row(page, prefix)).ToHaveCountAsync(2);
        foreach (var row in await Row(page, prefix).AllAsync())
        {
            await row.Locator("td:nth-child(1) .ant-checkbox").ClickAsync();
        }

        await page.GetByRole(AriaRole.Region, new() { Name = "批次動作" }).GetByRole(AriaRole.Button, new() { Name = "刪除所選" }).ClickAsync();
        await ConfirmAsync(page, "刪除");

        await Expect(Row(page, prefix)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "TDO-08")]
    [Trait("Title", "檢視者沒有新增按鈕、不能勾完成")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "檢視者")]
    public async Task Todo_Viewer_IsReadOnly()
    {
        var page = await LoginAsAsync(Seed.Viewer);
        await GotoAsync(page, "/todos", "待辦事項");
        await SearchAsync(page, E2ESeedData.SharedTodo);
        await Expect(Row(page, E2ESeedData.SharedTodo)).ToBeVisibleAsync();

        await Expect(ToolbarButton(page, "新增")).ToHaveCountAsync(0);
        await Expect(Row(page, E2ESeedData.SharedTodo).Locator(".ant-checkbox-disabled").First).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "TDO-09")]
    [Trait("Title", "選好截止日後在日期欄按 Enter 不會送出表單，日期有存進去")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_EnterInDueDate_ShouldNotSubmit()
    {
        // 1.0.62 修正：以前在截止日按 Enter 會直接送出、打的日期沒套用（M0 端對端測試發現）。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/todos", "待辦事項");
        var title = Unique("E2E 截止日Enter");
        var due = DateTime.Today.AddDays(10);

        await ToolbarButton(page, "新增").ClickAsync();
        await page.GetByPlaceholder("請輸入待辦標題").FillAsync(title);
        await PickAsync(page, SelectByLabel(page, "所屬專案"), E2ESeedData.SharedProject);
        await PickDateAsync(page, "截止日", due);
        await DateInput(page, "截止日").PressAsync("Enter");
        await page.WaitForTimeoutAsync(1000);

        // 視窗還開著、沒有新增
        await Expect(page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "截止日" })).ToBeVisibleAsync();
        await using (var db = App.CreateDbContext())
        {
            Assert.False(await db.Todo.AnyAsync(x => x.Title == title), "按 Enter 不該送出表單");
        }

        await ClickModalOkAsync(page);
        await Expect(Notice(page, "新增成功")).ToBeVisibleAsync();
        await using (var db = App.CreateDbContext())
        {
            Assert.Equal(due, (await db.Todo.AsNoTracking().SingleAsync(x => x.Title == title)).DueDate);
        }
    }

    [E2EFact]
    [Trait("Scenario", "TDO-10")]
    [Trait("Title", "按 Enter 送出後立刻再按「確定」只新增一筆")]
    [Trait("Page", "待辦事項")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Todo_EnterThenOk_ShouldCreateOnlyOne()
    {
        // 1.0.62 修正：Enter 送出前會等 200ms 回寫，以前等候不在存檔防護裡，這段期間按「確定」會新增兩筆。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/todos", "待辦事項");
        var title = Unique("E2E 連按");

        await ToolbarButton(page, "新增").ClickAsync();
        await PickAsync(page, SelectByLabel(page, "所屬專案"), E2ESeedData.SharedProject);
        var titleInput = page.GetByPlaceholder("請輸入待辦標題");
        await titleInput.FillAsync(title);
        await titleInput.PressAsync("Enter");
        await page.Locator(".ant-modal-wrap:visible .ant-modal-footer .ant-btn-primary").Last.ClickAsync(new() { NoWaitAfter = true, Timeout = 2000 }).ContinueWith(_ => { });
        await Expect(Notice(page, "新增成功")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1500);

        await using var db = App.CreateDbContext();
        Assert.Equal(1, await db.Todo.CountAsync(x => x.Title == title));
    }

    /// <summary>在「E2E 共同專案」新增一筆待辦，回傳標題（搜尋框已填這個標題）。</summary>
    private async Task<string> CreateTodoAsync(IPage page, string prefix, string? exactTitle = null, DateTime? dueDate = null)
    {
        var title = exactTitle ?? Unique(prefix);
        if (!page.Url.Contains("/todos", StringComparison.OrdinalIgnoreCase))
        {
            await GotoAsync(page, "/todos", "待辦事項");
        }

        await ToolbarButton(page, "新增").ClickAsync();
        await page.GetByPlaceholder("請輸入待辦標題").FillAsync(title);
        await PickAsync(page, SelectByLabel(page, "所屬專案"), E2ESeedData.SharedProject);
        if (dueDate is { } date)
        {
            await SetDueDateAsync(page, date);
        }

        await ClickModalOkAsync(page);
        await Expect(Notice(page, "新增成功")).ToBeVisibleAsync();
        await SearchAsync(page, title);
        await Expect(Row(page, title).First).ToBeVisibleAsync();
        return title;
    }

    private static Task SetDueDateAsync(IPage page, DateTime date) => PickDateAsync(page, "截止日", date);

    private static async Task SearchAsync(IPage page, string keyword)
    {
        await page.GetByPlaceholder("搜尋關鍵字").FillAsync(keyword);
        await ToolbarButton(page, "搜尋").ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待的狀態沒有出現。");
            }

            await Task.Delay(300);
        }
    }
}
