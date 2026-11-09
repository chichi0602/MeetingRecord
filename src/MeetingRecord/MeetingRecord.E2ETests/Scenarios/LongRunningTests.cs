using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>
/// L4 即時與長時間功能（1.0.66）：大檔上傳、斷線續傳、取消、進度推播、斷線重連、登入過期。
/// 見 docs/planning/09-Blazor-WASM-遷移評估與測試策略.md §8 L4——WASM 遷移時這一層最容易壞。
///
/// <para>
/// 本機上傳很快、假 AI 轉錄瞬間完成，要看到「上傳中」或來得及按取消，用 <see cref="E2ETestBase.ThrottleUploadsAsync"/> 放慢分塊；
/// 要模擬上傳時網路中斷，只攔上傳分塊（不動 Blazor 連線），才能分開驗證「上傳重試」與「連線重連」。
/// </para>
/// </summary>
public sealed partial class LongRunningTests(E2EAppFixture app) : E2ETestBase(app)
{
    private const int MB = 1024 * 1024;
    private const float UploadTimeout = 120_000;

    [E2EFact]
    [Trait("Scenario", "L4-01")]
    [Trait("Title", "200 MB 影音檔上傳完成，伺服器上的檔案與原檔逐位元相同")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task LargeUpload_ShouldStoreIdenticalFile()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var (title, media) = await FillNewMeetingWithMediaAsync(page, 200 * MB);

        await SaveNewMeetingWithMediaAsync(page, title);
        await Expect(Notice(page, "影音檔已上傳")).ToBeVisibleAsync(new() { Timeout = UploadTimeout });
        await WaitForRowTextAsync(page, title, "已完成");

        await AssertStoredMediaMatchesAsync(title, media);
    }

    [E2EFact]
    [Trait("Scenario", "L4-02")]
    [Trait("Title", "上傳途中網路中斷 5 秒，恢復後自動續傳完成、檔案無誤")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task UploadInterruptedFiveSeconds_ShouldRetryAndComplete()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var (title, media) = await FillNewMeetingWithMediaAsync(page, 64 * MB);

        // 第 3 塊（index 2）起的 5 秒內，所有分塊都當成網路斷掉。chunk-upload.js 每塊重試 1／3／9 秒，撐得過去。
        var gate = new object();
        DateTime? outageEnds = null;
        var aborted = 0;
        await page.RouteAsync("**/api/v1/uploads/**", async route =>
        {
            var abort = false;
            lock (gate)
            {
                if (outageEnds is null && ChunkIndex(route.Request.Url) >= 2)
                {
                    outageEnds = DateTime.UtcNow.AddSeconds(5);
                }

                if (outageEnds is { } end && DateTime.UtcNow < end)
                {
                    abort = true;
                    aborted++;
                }
            }

            if (abort)
            {
                await route.AbortAsync("internetdisconnected");
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        await SaveNewMeetingWithMediaAsync(page, title);
        await Expect(Notice(page, "影音檔已上傳")).ToBeVisibleAsync(new() { Timeout = UploadTimeout });
        await WaitForRowTextAsync(page, title, "已完成");

        Assert.True(aborted >= 2, $"應該至少中斷並重試兩次，實際只攔下 {aborted} 次（中斷沒有發生，這則測不到重試）");
        await AssertStoredMediaMatchesAsync(title, media);
    }

    [E2EFact]
    [Trait("Scenario", "L4-03")]
    [Trait("Title", "從右下角面板取消上傳：剛新增的會議一併刪除、不留檔案")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task CancelUploadFromJobPanel_ShouldRemoveMeetingAndFile()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var (title, _) = await FillNewMeetingWithMediaAsync(page, 40 * MB);
        var filesBefore = MediaFiles();
        await ThrottleUploadsAsync(page, TimeSpan.FromSeconds(1.5));

        await SaveNewMeetingWithMediaAsync(page, title);
        var row = JobPanelRow(page, title);
        await Expect(row).ToContainTextAsync("上傳中");
        await row.Locator(".job-progress-panel-row-cancel").ClickAsync();

        await Expect(Notice(page, "已取消上傳，這筆會議紀錄已一併移除。")).ToBeVisibleAsync();
        await Expect(Row(page, title)).ToHaveCountAsync(0);
        await using (var db = App.CreateDbContext())
        {
            Assert.False(await db.Meeting.AnyAsync(x => x.Title == title), "取消後會議應該一併刪除");
        }

        Assert.Equal(filesBefore, MediaFiles());
    }

    [E2EFact]
    [Trait("Scenario", "L4-04")]
    [Trait("Title", "上傳進度即時推給協作團隊成員（不必重新整理），沒有團隊的人看不到")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責＋協作＋無團隊")]
    public async Task UploadProgress_ShouldPushToCollaborator_NotToNoTeamUser()
    {
        // 從會議頁新增的會議沒有專案（只有上傳者看得到進度），所以用一筆已歸屬共同專案的會議換影音檔。
        var title = Unique("E2E 推播會議");
        await using (var db = App.CreateDbContext())
        {
            db.Meeting.Add(new Meeting { Title = title, ProjectId = Seed.SharedProjectId, CreatedByUserId = Seed.Owner.Id, MeetingDate = DateTime.Today });
            await db.SaveChangesAsync();
        }

        var collaborator = await LoginAsAsync(Seed.Collaborator);
        await GotoAsync(collaborator, "/meetings", "會議標題");
        var noTeam = await LoginAsAsync(Seed.NoTeam);
        await GotoAsync(noTeam, "/meetings", "會議標題");

        var owner = await LoginAsAsync(Seed.Owner);
        await GotoAsync(owner, "/meetings", "會議標題");
        await ThrottleUploadsAsync(owner, TimeSpan.FromSeconds(1.5));
        await RowAction(Row(owner, title), "修改").ClickAsync();
        await owner.Locator(".ant-modal-wrap:visible input[type=file]").SetInputFilesAsync(FakeMediaFile($"{title}.mp3", 40 * MB));
        await Expect(owner.GetByText("待上傳：")).ToBeVisibleAsync();
        await ClickModalOkAsync(owner);
        await ConfirmAsync(owner, "儲存並開始轉錄");

        // 協作成員的頁面不動，面板自己冒出這一列。
        await Expect(JobPanelRow(collaborator, title)).ToContainTextAsync("上傳中");
        await Expect(Notice(owner, "影音檔已上傳")).ToBeVisibleAsync(new() { Timeout = UploadTimeout });

        // 無團隊者的頁面還連著（按得動重新整理），而且自始至終沒有這一列。
        await noTeam.GetByRole(AriaRole.Button, new() { Name = "重新整理" }).ClickAsync();
        await Expect(noTeam.Locator(".ant-table-tbody")).ToBeVisibleAsync();
        await Expect(JobPanelRow(noTeam, title)).ToHaveCountAsync(0);
    }

    [E2EFact]
    [Trait("Scenario", "L4-05")]
    [Trait("Title", "斷網 10 秒後自動重連，頁面沒有重新載入、仍可操作")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task OfflineTenSeconds_ShouldReconnectWithoutReload()
    {
        var page = await LoginAsAsync(Seed.Owner);

        // ⚠️ 瀏覽器的離線模擬不會切斷已經建立的 WebSocket（Blazor 照樣連著）：Blazor 連線改經 Playwright 轉接，斷網時自己切斷。
        // 轉接要在連線建立前就掛上，所以放在進頁面之前。
        IWebSocketRoute? blazorSocket = null;
        await page.RouteWebSocketAsync(new Regex("/_blazor"), socket =>
        {
            socket.ConnectToServer();
            blazorSocket = socket;
        });
        await GotoAsync(page, "/meetings", "會議標題");
        await page.EvaluateAsync("() => window.__e2eMarker = 'L4-05'");

        await page.Context.SetOfflineAsync(true);
        Assert.NotNull(blazorSocket);
        await blazorSocket!.CloseAsync();
        var reconnect = page.Locator("#components-reconnect-modal");
        await Expect(reconnect).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.WaitForTimeoutAsync(10_000);
        await page.Context.SetOfflineAsync(false);

        await Expect(reconnect).ToBeHiddenAsync(new() { Timeout = 60_000 });
        Assert.Equal("L4-05", await page.EvaluateAsync<string?>("() => window.__e2eMarker ?? null"));
        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await Expect(page.GetByPlaceholder("請輸入會議標題")).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "L4-06")]
    [Trait("Title", "登入過期（Cookie 不見）後，下一次進頁面導回登入頁")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ExpiredLogin_NextNavigation_ShouldGoToLogin()
    {
        var page = await LoginAsAsync(Seed.Owner);
        await GotoAsync(page, "/meetings", "會議標題");

        await page.Context.ClearCookiesAsync();
        await page.GotoAsync("/meetings");

        await page.WaitForURLAsync(new Regex("/Auths/Login", RegexOptions.IgnoreCase), new() { Timeout = Timeout });
    }

    [E2EFact]
    [Trait("Scenario", "L4-07")]
    [Trait("Title", "登入過期時上傳影音檔：提示登入已失效、會議保留，不會卡在上傳中")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ExpiredLogin_Upload_ShouldFailCleanly()
    {
        var page = await LoginAsAsync(Seed.Owner);
        var (title, _) = await FillNewMeetingWithMediaAsync(page, 20 * MB);

        await page.Context.ClearCookiesAsync();
        await SaveNewMeetingWithMediaAsync(page, title);

        // 1.0.67：要講明是登入失效，使用者才知道要重新登入（以前只說「影音檔上傳失敗」）。
        await Expect(Notice(page, "會議資料已保留")).ToContainTextAsync("登入已失效", new() { Timeout = 60_000 });
        await Expect(Row(page, title)).Not.ToContainTextAsync("上傳中");
        await using var db = App.CreateDbContext();
        var meeting = await db.Meeting.AsNoTracking().SingleAsync(x => x.Title == title);
        Assert.Null(meeting.MediaRelativePath);
    }

    /// <summary>從資料庫找到這場會議存在伺服器上的影音檔，比對大小與 SHA-256。</summary>
    private async Task AssertStoredMediaMatchesAsync(string title, string original)
    {
        await using var db = App.CreateDbContext();
        var meeting = await db.Meeting.AsNoTracking().SingleAsync(x => x.Title == title);
        Assert.Equal(new FileInfo(original).Length, meeting.MediaFileSize);
        Assert.False(string.IsNullOrEmpty(meeting.MediaRelativePath), "上傳完成後應該記下影音檔位置");

        if (App.MeetingMediaPath is null)
        {
            return; // 對外部網站測：拿不到伺服器上的檔案，只比大小。
        }

        var stored = Path.Combine(App.MeetingMediaPath, meeting.MediaRelativePath!);
        Assert.Equal(await Sha256Async(original), await Sha256Async(stored));
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    /// <summary>伺服器影音資料夾目前所有檔案（相對路徑排序）；對外部網站測時為空。</summary>
    private List<string> MediaFiles()
        => App.MeetingMediaPath is { } root && Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(root, x)).Order().ToList()
            : [];

    private static int ChunkIndex(string url)
        => ChunkPattern().Match(url) is { Success: true } match ? int.Parse(match.Groups[1].Value) : -1;

    [GeneratedRegex(@"/chunks/(\d+)(?:\?|$)")]
    private static partial Regex ChunkPattern();
}
