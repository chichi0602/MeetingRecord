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
    [Trait("Page", "稽核紀錄")]
    [Trait("Persona", "管理者")]
    public async Task OfflineTenSeconds_ShouldReconnectWithoutReload()
    {
        // 1.1.23 會議頁搬到 WASM（沒有 Blazor Server 連線），這則改在還是 Blazor Server 的頁面驗：1.1.26 改用使用說明頁，
        // 1.1.30 使用說明頁也搬了，改用稽核紀錄頁（M5 才搬，只限管理者，所以換管理者登入）。WASM 頁的斷網見 L4-10。
        var page = await LoginAsAsync(Seed.Admin);

        // ⚠️ 瀏覽器的離線模擬不會切斷已經建立的 WebSocket（Blazor 照樣連著）：Blazor 連線改經 Playwright 轉接，斷網時自己切斷。
        // 轉接要在連線建立前就掛上，所以放在進頁面之前。
        IWebSocketRoute? blazorSocket = null;
        await page.RouteWebSocketAsync(new Regex("/_blazor"), socket =>
        {
            socket.ConnectToServer();
            blazorSocket = socket;
        });
        await GotoAsync(page, "/audit-logs", "發生時間");
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
        // 仍可操作：用一個不存在的關鍵字查詢，清單要變成空的（查詢經 Blazor 連線送到伺服器再畫回來，連線沒恢復就不會變）。
        var keyword = page.GetByPlaceholder("關鍵字（動作／目標／摘要／IP）");
        await keyword.FillAsync($"斷線後-{Guid.NewGuid():N}");
        await keyword.DispatchEventAsync("change");
        await ToolbarButton(page, "查詢").ClickAsync();
        await Expect(page.Locator(".ant-table .ant-empty")).ToBeVisibleAsync();
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
        try
        {
            await page.GotoAsync("/meetings");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_ABORTED"))
        {
            // 1.1.23 會議頁是 WASM 頁：清掉 Cookie 後，頁面背景的 API 呼叫（例如推播觸發的進度查詢）拿到 401 會自己先導到登入頁，
            // 打斷這次進頁面。終點一樣是登入頁，所以不算失敗。
        }

        await page.WaitForURLAsync(new Regex("/Auths/Login", RegexOptions.IgnoreCase), new() { Timeout = Timeout });
    }

    [E2EFact]
    [Trait("Scenario", "L4-07")]
    [Trait("Title", "上傳途中登入過期：導回登入頁，會議保留、沒有存進半個影音檔")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task ExpiredLogin_Upload_ShouldFailCleanly()
    {
        // 1.1.23 會議頁搬到 WASM：存檔、查進度都是 API 呼叫，登入一失效就整頁導回登入頁（以前停在頁面上顯示「登入已失效」）。
        // 所以改成上傳途中才讓登入失效：要能導得回登入頁（上傳中的「確定要離開嗎？」不能擋住），會議保留、沒有存進影音檔。
        var page = await LoginAsAsync(Seed.Owner);
        var (title, _) = await FillNewMeetingWithMediaAsync(page, 40 * MB);
        await ThrottleUploadsAsync(page, TimeSpan.FromSeconds(1.5));

        await SaveNewMeetingWithMediaAsync(page, title);
        await Expect(JobPanelRow(page, title)).ToContainTextAsync("上傳中");
        await page.Context.ClearCookiesAsync();

        await page.WaitForURLAsync(new Regex("/Auths/Login", RegexOptions.IgnoreCase), new() { Timeout = 60_000 });
        await using var db = App.CreateDbContext();
        var meeting = await db.Meeting.AsNoTracking().SingleAsync(x => x.Title == title);
        Assert.Null(meeting.MediaRelativePath);
    }

    [E2EFact]
    [Trait("Scenario", "L4-08")]
    [Trait("Title", "上傳中要切到舊版（Blazor Server）頁或關分頁會先確認；選留下上傳照常完成")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task LeavingDuringUpload_ShouldAskFirst()
    {
        // 1.1.6（WASM 遷移）：兩種頁面之間切換是整頁載入，上傳會中斷，所以離開前要先問。
        // 1.1.23 會議頁搬到 WASM：上傳改由整個前端共用，WASM 頁之間切換不中斷（見 L4-09），要問的是往還沒遷移的舊版頁
        // （1.1.26 起用使用說明頁，1.1.30 使用說明頁也搬了，改用稽核紀錄頁；確認框在導覽之前出現、選留下不會真的進頁，所以沒有那頁的權限也不影響）。
        var page = await LoginAsAsync(Seed.Owner);
        var (title, _) = await FillNewMeetingWithMediaAsync(page, 40 * MB);
        await ThrottleUploadsAsync(page, TimeSpan.FromSeconds(3));
        Assert.False(await BeforeUnloadBlockedAsync(page), "還沒上傳就攔了關分頁");

        await SaveNewMeetingWithMediaAsync(page, title);
        await Expect(JobPanelRow(page, title)).ToContainTextAsync("上傳中");

        // 關分頁、重新整理：beforeunload 被攔下（瀏覽器會問「確定要離開嗎？」）。
        // 用事件驗而不真的關頁：Playwright 觸發關頁後會中斷攔截中的上傳請求。
        Assert.True(await BeforeUnloadBlockedAsync(page), "上傳中關分頁沒有提醒");

        // 切到舊版頁：系統的確認框，選留下就不離開。
        await page.EvaluateAsync("() => Blazor.navigateTo('/audit-logs')");
        await Expect(page.Locator(".ant-modal-confirm").GetByText("上傳還在進行")).ToBeVisibleAsync();
        await ConfirmAsync(page, "留下");
        await Expect(page).ToHaveURLAsync(new Regex("/meetings$"));

        await Expect(Notice(page, "影音檔已上傳")).ToBeVisibleAsync(new() { Timeout = UploadTimeout });

        // 上傳結束就解除提醒，之後關分頁不會再問。
        var released = DateTime.UtcNow.AddSeconds(10);
        while (await BeforeUnloadBlockedAsync(page))
        {
            Assert.True(DateTime.UtcNow < released, "上傳完成後關分頁還在提醒");
            await page.WaitForTimeoutAsync(200);
        }
    }

    [E2EFact]
    [Trait("Scenario", "L4-09")]
    [Trait("Title", "上傳中換到其他 WASM 頁不中斷、不必確認，回來看到轉錄完成、檔案無誤")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task SwitchingWasmPagesDuringUpload_ShouldKeepUploading()
    {
        // 1.1.23：上傳由整個前端共用（MeetingUploadManager），換 WASM 頁不是整頁載入，上傳照樣繼續。
        var page = await LoginAsAsync(Seed.Owner);
        var (title, media) = await FillNewMeetingWithMediaAsync(page, 40 * MB);
        await ThrottleUploadsAsync(page, TimeSpan.FromSeconds(1));
        await SaveNewMeetingWithMediaAsync(page, title);
        await Expect(JobPanelRow(page, title)).ToContainTextAsync("上傳中");
        await page.EvaluateAsync("() => window.__e2eMarker = 'L4-09'");

        await page.EvaluateAsync("() => Blazor.navigateTo('/todos')");
        await Expect(page).ToHaveURLAsync(new Regex("/todos$"));
        await Expect(page.Locator(".ant-modal-confirm")).ToHaveCountAsync(0);
        await Expect(Notice(page, "影音檔已上傳")).ToBeVisibleAsync(new() { Timeout = UploadTimeout });
        Assert.Equal("L4-09", await page.EvaluateAsync<string?>("() => window.__e2eMarker ?? null"));

        await page.EvaluateAsync("() => Blazor.navigateTo('/meetings')");
        await WaitForRowTextAsync(page, title, "已完成");
        await AssertStoredMediaMatchesAsync(title, media);
    }

    [E2EFact]
    [Trait("Scenario", "L4-10")]
    [Trait("Title", "WASM 頁推播通道：一開始連不上、或斷網 10 秒，都會自動連上，頁面沒有重新載入、仍可操作")]
    [Trait("Page", "會議紀錄")]
    [Trait("Persona", "主責團隊成員")]
    public async Task WasmPage_PushChannel_ShouldRecoverFromStartFailureAndOutage()
    {
        // 1.1.23：WASM 頁沒有 Blazor Server 連線，要撐過網路問題的是推播通道（/hubs/app，進度面板、通知鈴靠它）。
        var page = await LoginAsAsync(Seed.Owner);
        var sockets = 0;
        IWebSocketRoute? hubSocket = null;
        var handshake = new TaskCompletionSource();
        page.WebSocket += (_, ws) =>
        {
            if (ws.Url.Contains("/hubs/app"))
            {
                ws.FrameReceived += (_, _) => handshake.TrySetResult();
            }
        };
        await page.RouteWebSocketAsync(new Regex("/hubs/app"), socket =>
        {
            socket.ConnectToServer();
            hubSocket = socket;
            Interlocked.Increment(ref sockets);
        });

        // 一開始連不上（協商被擋）：頁面照常可用，解除後背景重試會自己連上（以前第一次失敗就再也不試）。
        await page.RouteAsync("**/hubs/app/negotiate**", route => route.AbortAsync("internetdisconnected"));
        await GotoAsync(page, "/meetings", "會議標題");
        await page.WaitForTimeoutAsync(1000);
        Assert.Equal(0, Volatile.Read(ref sockets));
        await page.UnrouteAsync("**/hubs/app/negotiate**");
        await WaitUntilAsync(() => Volatile.Read(ref sockets) >= 1, "協商恢復後推播通道沒有自己連上");
        await handshake.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await page.EvaluateAsync("() => window.__e2eMarker = 'L4-10'");

        // 連上之後斷網 10 秒。同 L4-05，瀏覽器的離線模擬不會切斷已建立的 WebSocket，所以經 Playwright 轉接、斷網時自己切斷。
        await page.Context.SetOfflineAsync(true);
        await hubSocket!.CloseAsync();
        await page.WaitForTimeoutAsync(10_000);
        await page.Context.SetOfflineAsync(false);

        await WaitUntilAsync(() => Volatile.Read(ref sockets) >= 2, "恢復連網後推播通道沒有自動重連");
        Assert.Equal("L4-10", await page.EvaluateAsync<string?>("() => window.__e2eMarker ?? null"));
        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await Expect(page.GetByPlaceholder("請輸入會議標題")).ToBeVisibleAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(200);
        }
    }

    /// <summary>送一個可取消的 beforeunload 事件，看有沒有被攔下（也就是瀏覽器會不會問「確定要離開嗎？」）。</summary>
    private static Task<bool> BeforeUnloadBlockedAsync(IPage page)
        => page.EvaluateAsync<bool>("() => { const e = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented; }");

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
