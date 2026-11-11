using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>只限管理者的監控與管理頁：管理者打得開、一般使用者直接輸入網址也看不到內容。</summary>
public sealed class MonitoringTests(E2EAppFixture app) : E2ETestBase(app)
{
    /// <summary>頁面網址 → 只有管理者才看得到的文字。</summary>
    public static TheoryData<string, string> AdminPages => new()
    {
        { "/system-health", "重新檢查" },
        { "/logs", "全部展開" },
        { "/log-level-setting", "選擇要輸出的最低等級" },
        { "/database-usage", "檔案儲存用量" },
        { "/system-exceptions", "全部清除" },
        { "/token-usage", "篩選分析" },
        { "/ai-call-logs", "AI 對話紀錄" },
        { "/audit-logs", "發生時間" },
    };

    [E2ETheory]
    [MemberData(nameof(AdminPages))]
    [Trait("Scenario", "MON-01")]
    [Trait("Title", "管理者打得開監控與管理頁")]
    [Trait("Page", "監控與診斷")]
    [Trait("Persona", "管理者")]
    public async Task AdminPage_ShouldOpenForAdmin(string path, string adminOnlyText)
    {
        var page = await LoginAsAsync(Seed.Admin);

        await page.GotoAsync(path);

        await Expect(page.GetByText(adminOnlyText).First).ToBeVisibleAsync();
    }

    [E2ETheory]
    [MemberData(nameof(AdminPages))]
    [Trait("Scenario", "MON-02")]
    [Trait("Title", "一般使用者直接輸入網址看不到監控與管理頁內容")]
    [Trait("Page", "監控與診斷")]
    [Trait("Persona", "主責團隊成員")]
    public async Task AdminPage_ShouldStayHiddenFromNormalUser(string path, string adminOnlyText)
    {
        var page = await LoginAsAsync(Seed.Owner);

        await page.GotoAsync(path);
        // 等頁面畫好（使用者名稱出現在頁首）再判斷內容不存在，避免連線還沒建立就誤判成「看不到」。
        await Expect(page.GetByText(Seed.Owner.Name).First).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1500);

        await Expect(page.GetByRole(AriaRole.Button, new() { Name = adminOnlyText }).Or(page.GetByText(adminOnlyText, new() { Exact = true }))).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "AIM-01")]
    [Trait("Title", "Token 用量（WASM 頁）：明細看得到單筆內容、匯出 CSV 由伺服器產生並留稽核")]
    [Trait("Page", "Token 用量")]
    [Trait("Persona", "管理者")]
    public async Task TokenUsage_DetailAndCsvExport_ShouldWork()
    {
        // 1.1.37 Token 用量頁搬到 WASM：明細、匯出改走 api/v1/ai-usage，CSV 由伺服器產生、稽核由伺服器寫。
        var model = $"e2e-model-{Guid.NewGuid():N}"[..20];
        await using (var seed = App.CreateDbContext())
        {
            seed.AiUsageLog.Add(new MeetingRecord.AccessDatas.Models.AiUsageLog
            {
                OccurredAt = DateTime.Now,
                Feature = MeetingRecord.Share.Enums.AiUsageFeature.AiChat,
                Outcome = MeetingRecord.Share.Enums.AiUsageOutcome.Succeeded,
                Provider = "Fake",
                Model = model,
                InputTokens = 1234,
                OutputTokens = 56,
            });
            await seed.SaveChangesAsync();
        }

        var page = await LoginAsAsync(Seed.Admin);
        await page.GotoAsync("/token-usage");
        await Expect(page.GetByText("篩選分析").First).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new System.Text.RegularExpressions.Regex("^明細") }).ClickAsync();
        await RowAction(Row(page, model), "查看").ClickAsync();
        var detail = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = model });
        await Expect(detail).ToBeVisibleAsync();
        await detail.Locator(".ant-modal-close").ClickAsync();
        await Expect(detail).ToBeHiddenAsync();

        var fileName = await DownloadAsync(page, () => ToolbarButton(page, "匯出 CSV").ClickAsync());
        Assert.EndsWith(".csv", fileName);

        await using var db = App.CreateDbContext();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await db.AuditLog.AnyAsync(x => x.Action == "AiUsage.Export" && x.Detail!.EndsWith("via=Api")))
        {
            Assert.True(DateTime.UtcNow < deadline, "匯出 CSV 要由伺服器寫稽核");
            await Task.Delay(300);
        }
    }
}
