using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>專案項目頁：主責＋協作團隊可見性、新增、封存唯讀、PDF、附件分塊上傳、負責人規則。</summary>
public sealed class ProjectTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2ETheory]
    [InlineData("主責團隊成員")]
    [InlineData("協作團隊成員")]
    [InlineData("甲方")]
    [Trait("Scenario", "PRJ-01")]
    [Trait("Title", "專案選單只列出主責或協作團隊有自己的專案")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "依人物")]
    public async Task ProjectSelector_ShouldFollowTeamVisibility(string persona)
    {
        var (user, sees, hides) = persona switch
        {
            "主責團隊成員" => (Seed.Owner, new[] { E2ESeedData.SharedProject }, new[] { E2ESeedData.TeamBProject }),
            "協作團隊成員" => (Seed.Collaborator, new[] { E2ESeedData.SharedProject, E2ESeedData.TeamBProject }, Array.Empty<string>()),
            _ => (Seed.Client, new[] { E2ESeedData.SharedProject, E2ESeedData.TeamBProject }, Array.Empty<string>()),
        };
        var page = await LoginAsAsync(user);
        await GotoAsync(page, "/projects", "專案");

        var options = await ProjectOptionsAsync(page);

        foreach (var title in sees)
        {
            Assert.Contains(options, o => o.Contains(title));
        }

        foreach (var title in hides)
        {
            Assert.DoesNotContain(options, o => o.Contains(title));
        }
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-02")]
    [Trait("Title", "沒有任何團隊的人看不到任何專案")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "無團隊使用者")]
    public async Task NoTeamUser_ShouldSeeNoProject()
    {
        var page = await LoginAsAsync(Seed.NoTeam);

        await page.GotoAsync("/projects");

        await Expect(page.GetByText("目前沒有可存取的專案")).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-03")]
    [Trait("Title", "新增專案：主責團隊只能選自己的團隊")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Project_Create()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        var title = Unique("E2E 新專案");

        await page.GetByRole(AriaRole.Button, new() { Name = "新增專案" }).ClickAsync();
        await page.GetByPlaceholder("請輸入專案標題").FillAsync(title);
        // 主責不預設（0.4.114 起一定要自己選）；非管理者只能選自己所屬的團隊。
        await SelectByLabel(page, "主責團隊").ClickAsync();
        var options = page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option");
        await Expect(options.Filter(new() { HasText = E2ESeedData.TeamA })).ToHaveCountAsync(1);
        await Expect(options.Filter(new() { HasText = E2ESeedData.TeamB })).ToHaveCountAsync(0);
        await options.Filter(new() { HasText = E2ESeedData.TeamA }).ClickAsync();
        await ClickModalOkAsync(page);

        await Expect(Notice(page, "新增成功")).ToBeVisibleAsync();
        Assert.Contains(await ProjectOptionsAsync(page), o => o.Contains(title));
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-04")]
    [Trait("Title", "已完成（封存）的專案對一般使用者唯讀")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ArchivedProject_ShouldBeReadOnly()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");

        await page.GetByRole(AriaRole.Radio, new() { Name = "已完成" }).ClickAsync();
        await SelectProjectAsync(page, E2ESeedData.ArchivedProject);

        await Expect(page.GetByText("目前是唯讀")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "重新開啟" })).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-05")]
    [Trait("Title", "專案待辦 PDF 總覽下載")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Project_TodoPdf()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        await SelectProjectAsync(page, E2ESeedData.SharedProject);

        var fileName = await DownloadAsync(page, () => page.GetByRole(AriaRole.Button, new() { Name = "待辦 PDF" }).ClickAsync());

        Assert.EndsWith(".pdf", fileName);
        Assert.Contains("待辦總覽", fileName);
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-06")]
    [Trait("Title", "歷史會議紀錄匯出 PDF")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Project_ExportMeetingMinutesPdf()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        await SelectProjectAsync(page, E2ESeedData.SharedProject);

        var fileName = await DownloadAsync(page,
            () => RowAction(Row(page, E2ESeedData.MinutesMeeting), "匯出 PDF").ClickAsync());

        Assert.EndsWith(".pdf", fileName);
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-07")]
    [Trait("Title", "專案附件上傳（分塊）並可下載")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Project_AttachmentUploadAndDownload()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/projects", "專案");
        await SelectProjectAsync(page, E2ESeedData.SharedProject);
        var fileName = $"{Unique("附件")}.pdf";
        var file = FakeMediaFile(fileName, 200 * 1024);

        await page.GetByRole(AriaRole.Button, new() { Name = "編輯專案" }).ClickAsync();
        await page.Locator(".ant-modal-wrap:visible input[type=file]").SetInputFilesAsync(file);
        await Expect(page.Locator(".ant-modal-wrap:visible").GetByText(fileName)).ToBeVisibleAsync();
        await ClickModalOkAsync(page);
        await Expect(Notice(page, "修改成功")).ToBeVisibleAsync();

        // 1.0.65：存檔後暫存檔要刪掉。以前串流還開著就刪，Windows 上刪不掉，每存一次留一份（以大小認出這一則的檔）。
        if (App.PendingUploadPath is { } pendingPath && Directory.Exists(pendingPath))
        {
            var leftovers = Directory.GetFiles(pendingPath).Where(x => new FileInfo(x).Length == 200 * 1024).ToList();
            Assert.True(leftovers.Count == 0, $"存檔後暫存檔沒有刪掉：{string.Join("、", leftovers)}");
        }

        await page.Locator(".project-view-summary-item").Filter(new() { HasText = "附件" }).GetByRole(AriaRole.Button).ClickAsync();
        var row = page.Locator(".project-view-attachment-row").Filter(new() { HasText = fileName });
        var downloaded = await DownloadAsync(page, () => RowAction(row, "下載").ClickAsync());

        Assert.Equal(fileName, downloaded);
    }

    [E2ETheory]
    [InlineData("主責團隊成員（負責人）", true)]
    [InlineData("協作團隊成員", false)]
    [Trait("Scenario", "PRJ-08")]
    [Trait("Title", "只有專案負責人能按「刪除專案」")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "依人物")]
    public async Task DeleteProject_OnlyForOwner(string persona, bool expected)
    {
        var page = await LoginAsAsync(expected ? Seed.Owner : Seed.Collaborator);
        await GotoAsync(page, "/projects", "專案");
        await SelectProjectAsync(page, E2ESeedData.SharedProject);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "編輯專案" })).ToBeVisibleAsync();

        // 有編輯權限的人都看得到按鈕，不是負責人時停用。
        var delete = page.GetByRole(AriaRole.Button, new() { Name = "刪除專案" });
        if (expected)
        {
            await Expect(delete).ToBeEnabledAsync();
        }
        else
        {
            await Expect(delete).ToBeDisabledAsync();
        }
        _ = persona;
    }

    [E2EFact]
    [Trait("Scenario", "PRJ-09")]
    [Trait("Title", "協作團隊成員不能更換主責團隊")]
    [Trait("Page", "專案項目")]
    [Trait("Persona", "協作團隊成員")]
    public async Task Collaborator_CannotChangePrimaryTeam()
    {
        var page = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(page, "/projects", "專案");
        await SelectProjectAsync(page, E2ESeedData.SharedProject);

        await page.GetByRole(AriaRole.Button, new() { Name = "編輯專案" }).ClickAsync();

        await Expect(SelectByLabel(page, "主責團隊")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("ant-select-disabled"));
    }

    private static async Task SelectProjectAsync(IPage page, string title)
        => await PickAsync(page, page.Locator(".project-view-project-select"), title);

    private static async Task<IReadOnlyList<string>> ProjectOptionsAsync(IPage page)
    {
        var select = page.Locator(".project-view-project-select");
        await select.ClickAsync();
        var options = page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option");
        await Expect(options.First).ToBeVisibleAsync();
        var texts = await options.AllInnerTextsAsync();
        await page.Keyboard.PressAsync("Escape");
        return texts;
    }
}
