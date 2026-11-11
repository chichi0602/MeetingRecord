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
        { "/ai-call-logs", "清除此日之前" }, // 1.1.39：原本的「AI 對話紀錄」側邊選單也有，頁面壞了也照過
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

    [E2EFact]
    [Trait("Scenario", "AIM-02")]
    [Trait("Title", "AI 對話紀錄（WASM 頁）：從呼叫識別碼直接開明細、查看與刪除的稽核由伺服器寫")]
    [Trait("Page", "AI 對話紀錄")]
    [Trait("Persona", "管理者")]
    public async Task AiCallLog_DeepLinkAndDelete_ShouldWork()
    {
        // 1.1.39 AI 對話紀錄頁搬到 WASM：明細、刪除改走 api/v1/ai-call-logs，查看也要由伺服器寫稽核（內容含逐字稿）。
        var model = $"e2e-call-{Guid.NewGuid():N}"[..20];
        var callId = Guid.NewGuid();
        int id;
        await using (var seed = App.CreateDbContext())
        {
            var row = new MeetingRecord.AccessDatas.Models.AiCallLog
            {
                CallId = callId,
                OccurredAt = DateTime.Now,
                Feature = MeetingRecord.Share.Enums.AiUsageFeature.AiChat,
                Outcome = MeetingRecord.Share.Enums.AiUsageOutcome.Succeeded,
                Provider = "Fake",
                Model = model,
                RelatedInfo = model,
            };
            seed.AiCallLog.Add(row);
            await seed.SaveChangesAsync();
            id = row.Id;
        }

        var page = await LoginAsAsync(Seed.Admin);
        await page.GotoAsync($"/ai-call-logs?callId={callId:D}");

        // 沒有內容檔：明細照樣打得開，只顯示摘要與提示。
        var detail = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = callId.ToString() });
        await Expect(detail).ToBeVisibleAsync();
        await Expect(detail.GetByText("內容檔不存在")).ToBeVisibleAsync();
        await detail.Locator(".ant-modal-close").ClickAsync();
        await Expect(detail).ToBeHiddenAsync();

        await RowAction(Row(page, model), "刪除").ClickAsync();
        await ConfirmAsync(page, "刪除");
        await Expect(Row(page, model)).ToHaveCountAsync(0);

        await using var db = App.CreateDbContext();
        var targetId = id.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(await db.AuditLog.AnyAsync(x => x.Action == "AiCallLog.View" && x.TargetId == targetId && x.Detail!.EndsWith("via=Api"))
                 && await db.AuditLog.AnyAsync(x => x.Action == "AiCallLog.Delete" && x.TargetId == targetId && x.Detail!.EndsWith("via=Api"))))
        {
            Assert.True(DateTime.UtcNow < deadline, "查看與刪除都要由伺服器寫稽核");
            await Task.Delay(300);
        }
    }

    [E2EFact]
    [Trait("Scenario", "EXC-01")]
    [Trait("Title", "系統例外（WASM 頁）：明細按 AI 分析邊產生邊顯示，稽核由伺服器寫")]
    [Trait("Page", "系統例外紀錄")]
    [Trait("Persona", "管理者")]
    public async Task ExceptionLog_AiAnalysis_ShouldStreamAndAudit()
    {
        // 1.1.41 系統例外頁搬到 WASM：AI 分析改走 api/v1/exception-logs 的串流，送出內容由伺服器組、稽核由伺服器寫。
        var message = $"e2e-exception-{Guid.NewGuid():N}";
        int id;
        await using (var seed = App.CreateDbContext())
        {
            var row = new MeetingRecord.AccessDatas.Models.ExceptionLog
            {
                Signature = Guid.NewGuid().ToString("N"),
                ExceptionType = "System.InvalidOperationException",
                Message = message,
                Source = "WebAPI",
                Page = "/api/v1/e2e",
                OccurrenceCount = 3,
                FirstOccurredAt = DateTime.Now.AddMinutes(-5),
                LastOccurredAt = DateTime.Now,
            };
            seed.ExceptionLog.Add(row);
            await seed.SaveChangesAsync();
            id = row.Id;
        }

        var page = await LoginAsAsync(Seed.Admin);
        await page.GotoAsync("/system-exceptions");
        await RowAction(Row(page, message), "查看").ClickAsync();
        var detail = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = message });
        await Expect(detail.GetByText("堆疊檔案不存在").First).ToBeVisibleAsync();

        await detail.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("AI 分析$") }).ClickAsync();
        await ConfirmAsync(page, "開始分析");
        var result = page.Locator(".ant-modal-wrap:visible").Filter(new() { HasText = "AI 例外分析結果" });
        await Expect(result.GetByText("E2E 假 AI 回應").First).ToBeVisibleAsync();
        await Expect(result.GetByText("System.InvalidOperationException").First).ToBeVisibleAsync();

        await using var db = App.CreateDbContext();
        var targetId = id.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await db.AuditLog.AnyAsync(x => x.Action == "ExceptionLog.AiAnalyze" && x.TargetId == targetId && x.Success && x.Detail!.EndsWith("via=Api")))
        {
            Assert.True(DateTime.UtcNow < deadline, "AI 分析的稽核要由伺服器寫");
            await Task.Delay(300);
        }
    }
}
