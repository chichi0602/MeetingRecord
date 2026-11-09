using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>站內通知鈴（跨瀏覽器即時推送）與儀表板（全公司彙總、看不到的專案名稱要藏起來）。</summary>
public sealed class NotificationAndDashboardTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "NTF-01")]
    [Trait("Title", "協作成員改了待辦狀態，負責人的通知鈴即時收到")]
    [Trait("Page", "通知鈴")]
    [Trait("Persona", "主責團隊成員＋協作團隊成員（兩個瀏覽器）")]
    public async Task Notification_ShouldReachProjectMemberInRealTime()
    {
        var title = $"E2E 通知待辦-{Guid.NewGuid().ToString("N")[..6]}";
        await using (var db = App.CreateDbContext())
        {
            db.Todo.Add(new Todo { Title = title, ProjectId = Seed.SharedProjectId, Status = "待辦", Priority = "中", Owner = Seed.Owner.Name, CreatedByUserId = Seed.Owner.Id });
            await db.SaveChangesAsync();
        }

        // 負責人先開著頁面（收通知的一方），協作成員在另一個瀏覽器改狀態。
        var owner = await LoginAsAsync(Seed.Owner);
        await GotoAsync(owner, "/dashboard", "營運概況");
        var collaborator = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(collaborator, "/todos", "待辦事項");
        await collaborator.GetByPlaceholder("搜尋關鍵字").FillAsync(title);
        await ToolbarButton(collaborator, "搜尋").ClickAsync();
        await RowAction(Row(collaborator, title), "修改").ClickAsync();
        await PickAsync(collaborator, SelectByLabel(collaborator, "狀態"), "進行中");
        await ClickModalOkAsync(collaborator);
        await Expect(Notice(collaborator, "修改成功")).ToBeVisibleAsync();

        var bell = owner.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^通知，\\d+ 則未讀$") });
        await Expect(bell).ToBeVisibleAsync();
        await bell.ClickAsync();
        await Expect(owner.GetByRole(AriaRole.Dialog, new() { Name = "通知" }).Locator(".notification-bell-message").Filter(new() { HasText = title }).First).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "DSH-01")]
    [Trait("Title", "儀表板顯示營運概況")]
    [Trait("Page", "儀表板")]
    [Trait("Persona", "檢視者")]
    public async Task Dashboard_ShouldShowOverview()
    {
        var page = await LoginAsAsync(Seed.Viewer);

        await GotoAsync(page, "/dashboard", "營運概況");

        foreach (var card in new[] { "專案總數", "會議紀錄", "待辦未完成" })
        {
            await Expect(page.GetByText(card).First).ToBeVisibleAsync();
        }
    }

    [E2ETheory]
    [InlineData("主責團隊成員", false)]
    [InlineData("管理者", true)]
    [Trait("Scenario", "DSH-02")]
    [Trait("Title", "儀表板圖表不露出看不到的專案名稱（併成「其他專案」）")]
    [Trait("Page", "儀表板")]
    [Trait("Persona", "依人物")]
    public async Task Dashboard_ShouldHideInvisibleProjectNames(string persona, bool seesTeamBProject)
    {
        var page = await LoginAsAsync(seesTeamBProject ? Seed.Admin : Seed.Owner);
        await GotoAsync(page, "/dashboard", "各專案的會議紀錄份數");
        await Expect(page.GetByText(E2ESeedData.SharedProject).First).ToBeVisibleAsync();

        if (seesTeamBProject)
        {
            await Expect(page.GetByText(E2ESeedData.TeamBProject).First).ToBeVisibleAsync();
        }
        else
        {
            await Expect(page.GetByText(E2ESeedData.TeamBProject)).ToHaveCountAsync(0);
            await Expect(page.GetByText("其他專案").First).ToBeVisibleAsync();
        }

        _ = persona;
    }
}
