using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>會議紀錄頁：可見範圍、新增、上傳轉錄、會議紀錄生成與匯出、上傳者與負責人規則。</summary>
public sealed class MeetingTests(E2EAppFixture app) : E2ETestBase(app)
{
    private const string Fake = "E2E 假逐字稿";

    [E2ETheory]
    [InlineData("主責團隊成員")]
    [InlineData("協作團隊成員")]
    [InlineData("無團隊使用者")]
    [Trait("Scenario", "MTG-01")]
    [Trait("Title", "會議清單只列出看得到的專案的會議")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "依人物")]
    public async Task MeetingList_ShouldFollowProjectVisibility(string persona)
    {
        var (user, sees, hides) = persona switch
        {
            "主責團隊成員" => (Seed.Owner, new[] { E2ESeedData.SharedMeeting }, new[] { E2ESeedData.TeamBMeeting }),
            "協作團隊成員" => (Seed.Collaborator, new[] { E2ESeedData.SharedMeeting, E2ESeedData.TeamBMeeting }, Array.Empty<string>()),
            _ => (Seed.NoTeam, Array.Empty<string>(), new[] { E2ESeedData.SharedMeeting, E2ESeedData.TeamBMeeting }),
        };
        var page = await LoginAsAsync(user);
        await GotoAsync(page, "/meetings", "會議標題");
        await Expect(page.Locator(".ant-table-tbody")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1000);

        foreach (var title in sees)
        {
            await Expect(Row(page, title)).ToBeVisibleAsync();
        }

        foreach (var title in hides)
        {
            await Expect(Row(page, title)).ToHaveCountAsync(0);
        }
    }

    [E2EFact]
    [Trait("Scenario", "MTG-02")]
    [Trait("Title", "新增會議（不帶影音檔）")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_AddWithoutMedia()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var title = Unique("E2E 新會議");

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入會議標題").FillAsync(title);
        await ClickModalOkAsync(page);

        await Expect(Row(page, title)).ToBeVisibleAsync();
        await Expect(Row(page, title)).ToContainTextAsync("尚未上傳");
    }

    [E2EFact]
    [Trait("Scenario", "MTG-03")]
    [Trait("Title", "上傳影音檔→背景轉錄完成→逐字稿可看、可下載")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_UploadTranscribeAndReadTranscript()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var title = await AddMeetingWithMediaAsync(page);

        await WaitForRowTextAsync(page, title, "已完成");
        await RowAction(Row(page, title), "會議紀錄與逐字稿").ClickAsync();
        await page.GetByRole(AriaRole.Tab, new() { Name = "逐字稿" }).ClickAsync();
        await Expect(page.Locator(".ant-modal-wrap:visible").GetByText(Fake).First).ToBeVisibleAsync();

        var fileName = await DownloadAsync(page, () => page.GetByRole(AriaRole.Button, new() { Name = "下載 .txt" }).ClickAsync());
        Assert.EndsWith(".txt", fileName);
    }

    [E2EFact]
    [Trait("Scenario", "MTG-04")]
    [Trait("Title", "產生會議紀錄（費用確認）→生成完成並顯示內容")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_GenerateMinutes()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var title = await AddMeetingWithMediaAsync(page);
        await WaitForRowTextAsync(page, title, "已完成");

        await RowAction(Row(page, title), "會議紀錄與逐字稿").ClickAsync();
        // 還沒有會議紀錄時視窗預設停在「逐字稿」頁籤。
        await page.GetByRole(AriaRole.Tab, new() { Name = "會議紀錄" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "產生會議紀錄" }).ClickAsync();
        // 「產生會議紀錄」設定視窗：欄位依序是來源逐字稿（文字）、提示詞、所屬專案、與會人員，第一個下拉就是提示詞。
        var settings = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "來源逐字稿" });
        await PickAsync(page, settings.Locator(".ant-select").First, E2ESeedData.PromptTemplate);
        await DialogButton(page, "開始產生").First.ClickAsync();
        await ConfirmAsync(page, "開始產生");

        // 欄位：勾選、會議標題、會議日期、分類、影音檔、轉錄狀態、會議紀錄。
        await WaitForRowTextAsync(page, title, "已完成", column: 7);
        await page.Keyboard.PressAsync("Escape");
        await RowAction(Row(page, title), "會議紀錄與逐字稿").ClickAsync();
        await Expect(page.Locator(".ant-modal-wrap:visible").GetByText("E2E 假 AI 回應").First).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "MTG-05")]
    [Trait("Title", "已歸屬專案的會議紀錄在會議頁唯讀，引導到專案頁編修與下載")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_AttachedMinutes_ShouldBeReadOnlyHere()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");

        await RowAction(Row(page, E2ESeedData.MinutesMeeting), "會議紀錄與逐字稿").ClickAsync();

        var modal = page.Locator(".ant-modal-wrap:visible");
        await Expect(modal.GetByText("王主責下週五前完成介面設計")).ToBeVisibleAsync();
        await Expect(modal.GetByText("編修與下載 PDF 請到「專案項目」頁")).ToBeVisibleAsync();
        await Expect(modal.GetByRole(AriaRole.Button, new() { Name = "編修" })).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MTG-06")]
    [Trait("Title", "協作團隊成員看得到別人上傳的會議，但不能刪除或重新轉錄")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "協作團隊成員")]
    public async Task Meeting_NonUploader_CannotDeleteOrRetranscribe()
    {
        var page = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(page, "/meetings", "會議標題");

        var row = Row(page, E2ESeedData.SharedMeeting);
        await Expect(row).ToBeVisibleAsync();
        await Expect(RowAction(row, "刪除")).ToHaveCountAsync(0);
        await Expect(RowAction(row, "重新轉錄")).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MTG-07")]
    [Trait("Title", "檢視者沒有新增按鈕")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "檢視者")]
    public async Task Meeting_Viewer_HasNoAddButton()
    {
        var page = await LoginAsAsync(Seed.Viewer);
        await GotoAsync(page, "/meetings", "會議標題");
        await Expect(Row(page, E2ESeedData.SharedMeeting)).ToBeVisibleAsync();

        await Expect(ToolbarButton(page, "新增")).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MTG-08")]
    [Trait("Title", "上傳者刪除自己的會議")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_UploaderDeletesOwnMeeting()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var title = Unique("E2E 要刪的會議");
        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入會議標題").FillAsync(title);
        await ClickModalOkAsync(page);
        await Expect(Row(page, title)).ToBeVisibleAsync();

        await RowAction(Row(page, title), "刪除").ClickAsync();
        await ConfirmAsync(page, "刪除");

        await Expect(Row(page, title)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "MTG-09")]
    [Trait("Title", "甲方沒有會議紀錄頁的權限")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "甲方")]
    public async Task Meeting_Client_ShouldBeDenied()
    {
        var page = await LoginAsAsync(Seed.Client);

        await page.GotoAsync("/meetings");

        await Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "你沒有權限存取此頁面" })).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "MTG-10")]
    [Trait("Title", "選好會議日期後在日期欄按 Enter 不會送出表單，日期有存進去")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Meeting_EnterInMeetingDate_ShouldNotSubmit()
    {
        // 1.0.62 修正：以前在會議日期按 Enter 會直接送出、打的日期沒套用（M0 端對端測試發現）。
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");
        var title = Unique("E2E 會議日期Enter");
        var date = DateTime.Today.AddDays(-3);

        await ToolbarButton(page, "新增").ClickAsync();
        await page.GetByPlaceholder("請輸入會議標題").FillAsync(title);
        await PickDateAsync(page, "會議日期", date);
        await DateInput(page, "會議日期").PressAsync("Enter");
        await page.WaitForTimeoutAsync(1000);

        await Expect(page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "會議日期" })).ToBeVisibleAsync();
        await using (var db = App.CreateDbContext())
        {
            Assert.False(await db.Meeting.AnyAsync(x => x.Title == title), "按 Enter 不該送出表單");
        }

        await ClickModalOkAsync(page);
        await Expect(Row(page, title)).ToBeVisibleAsync();
        await using (var db = App.CreateDbContext())
        {
            Assert.Equal(date, (await db.Meeting.AsNoTracking().SingleAsync(x => x.Title == title)).MeetingDate);
        }
    }
}
