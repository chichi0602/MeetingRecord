using System.Text.RegularExpressions;

namespace MeetingRecord.E2ETests.Infrastructure;

/// <summary>
/// 端對端測試的基底：每則測試自己的瀏覽器 context（不共用登入），結束時關掉。
/// 定位一律用角色、可見文字、placeholder（不加 data-testid），讓同一套腳本之後能直接對 WASM 版跑。
/// </summary>
[Collection(E2ECollection.Name)]
public abstract class E2ETestBase : IAsyncLifetime
{
    private readonly List<IBrowserContext> contexts = [];

    protected E2ETestBase(E2EAppFixture app)
    {
        App = app;
    }

    protected E2EAppFixture App { get; }

    protected E2ESeedData Seed => App.Seed;

    /// <summary>Blazor Server 關掉預先算繪，頁面要等連線建立才出現內容；預設等待放寬到 20 秒。</summary>
    protected const float Timeout = 20_000;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var context in contexts)
        {
            await context.CloseAsync();
        }
    }

    /// <summary>開一個新的瀏覽器 context 並登入成 <paramref name="persona"/>。</summary>
    protected async Task<IPage> LoginAsAsync(E2EPersona persona, bool rememberMe = false)
    {
        var page = await NewPageAsync();
        await SubmitLoginAsync(page, persona.Account, persona.Password, rememberMe);
        await page.WaitForURLAsync(url => !url.Contains("/Auths/Login", StringComparison.OrdinalIgnoreCase), new() { Timeout = Timeout });
        return page;
    }

    protected async Task<IPage> NewPageAsync()
    {
        var context = await App.NewContextAsync();
        context.SetDefaultTimeout(Timeout);
        contexts.Add(context);
        return await context.NewPageAsync();
    }

    /// <summary>填登入表單：驗證碼是畫面上印出來的 4 碼（只比對畫面上那組），照抄即可。</summary>
    protected static async Task SubmitLoginAsync(IPage page, string account, string password, bool rememberMe = false)
    {
        await page.GotoAsync("/Auths/Login");
        await page.Locator("#account").FillAsync(account);
        await page.Locator("#password").FillAsync(password);
        var captcha = (await page.Locator(".captcha-display").InnerTextAsync()).Trim();
        await page.Locator("#captcha").FillAsync(captcha);
        if (rememberMe)
        {
            // 勾選框用 opacity:0 藏起來、畫的是自訂樣式；靜態表單直接設 checked 送出即可。
            await page.Locator("#rememberMe").EvaluateAsync("el => el.checked = true");
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "登入系統" }).ClickAsync();
    }

    /// <summary>到某頁並等 Blazor 把內容畫出來（以頁面上一定會有的文字為準）。</summary>
    protected static async Task GotoAsync(IPage page, string path, string expectedText)
    {
        await page.GotoAsync(path);
        await Expect(page.GetByText(expectedText).First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await ClearJobPanelAsync(page);
    }

    /// <summary>
    /// 右下角的背景工作面板會蓋住表格右下角的按鈕；其他測試上傳、轉錄留下的列，這裡先全部關掉。
    /// （「關閉」只移除通知、不影響工作本身。）
    /// </summary>
    protected static async Task ClearJobPanelAsync(IPage page)
    {
        var closeAll = page.Locator(".job-progress-panel").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("全部關閉$") });
        if (await closeAll.CountAsync() > 0 && await closeAll.First.IsVisibleAsync())
        {
            await closeAll.First.ClickAsync();
        }
    }

    /// <summary>表格裡含有某段文字的那一列。</summary>
    protected static ILocator Row(IPage page, string text)
        => page.Locator("tr.ant-table-row").Filter(new() { HasText = text });

    /// <summary>右下角通知（AntDesign Notification／Message）裡的文字。</summary>
    protected static ILocator Notice(IPage page, string text)
        => page.Locator(".ant-notification-notice, .ant-message-notice").Filter(new() { HasText = text }).First;

    /// <summary>確認框的按鈕（AntDesign 會在兩個中文字的按鈕中間插空白，所以用正規式比對）。</summary>
    protected static ILocator DialogButton(IPage page, string text)
        => page.Locator(".ant-modal-wrap:visible .ant-btn").Filter(new() { HasTextRegex = new Regex($"^\\s*{string.Join("\\s*", text.ToCharArray().Select(c => Regex.Escape(c.ToString())))}\\s*$") });

    /// <summary>
    /// 有可見文字的按鈕（工具列、對話框）。可及名稱前面會帶 Material Icons 的圖示字（例如「add 新增」），
    /// 所以不能用 Exact；這裡允許前面有一段英文圖示名稱，其餘必須完全相同（「新增」不會配到「新增子項目」）。
    /// </summary>
    protected static ILocator ToolbarButton(IPage page, string text)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex($@"^([a-z_]+\s+)?{Regex.Escape(text)}$") });

    /// <summary>列上的動作按鈕（只有圖示、名稱來自隱藏文字），比對規則同 <see cref="ToolbarButton"/>。</summary>
    /// <remarks>AntDesign 固定在右側的欄位有時會畫兩份，取第一個。</remarks>
    protected static ILocator RowAction(ILocator row, string text)
        => row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex($@"^([a-z_]+\s+)?{Regex.Escape(text)}$") }).First;

    /// <summary>AntDesign Select：點開下拉，選標題等於 <paramref name="option"/> 的那一項。</summary>
    protected static async Task PickAsync(IPage page, ILocator select, string option)
    {
        await select.ClickAsync();
        var item = page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option")
            .Filter(new() { HasText = option }).First;
        await item.ClickAsync();
    }

    /// <summary>標籤為 <paramref name="label"/> 的表單欄位裡的 AntDesign Select。</summary>
    protected static ILocator SelectByLabel(IPage page, string label)
        => page.Locator(".ant-modal-wrap:visible .ant-form-item")
            .Filter(new() { Has = page.Locator("label", new() { HasTextRegex = new Regex($@"^\s*{Regex.Escape(label)}\s*$") }) })
            .Locator(".ant-select").First;

    /// <summary>表單視窗裡標籤為 <paramref name="label"/> 的日期欄輸入框。</summary>
    protected static ILocator DateInput(IPage page, string label)
        => page.Locator(".ant-modal-wrap:visible .ant-form-item").Filter(new() { HasText = label }).Locator("input").First;

    /// <summary>
    /// 在表單視窗的日期欄點日曆選日期（AntDesign DatePicker）。不用打字：打進去的文字套件不一定解析得到，
    /// 點日曆才穩定。日曆格的 title 是 zh-TW 格式（2026年10月25日）；不在這個月就按「下個月」往後翻。
    /// ⚠️ 翻頁後日曆是非同步重畫的：一定要等表頭的年月變了再判斷，否則會多翻一頁、點到錯的日子。
    /// </summary>
    protected static async Task PickDateAsync(IPage page, string label, DateTime date)
    {
        var input = DateInput(page, label);
        await input.ClickAsync();

        var dropdown = page.Locator(".ant-picker-dropdown:not(.ant-picker-dropdown-hidden)");
        var header = dropdown.Locator(".ant-picker-header-view").First;
        await Expect(dropdown.Locator("td.ant-picker-cell-in-view").First).ToBeVisibleAsync();
        var cell = dropdown.Locator($"td.ant-picker-cell-in-view[title=\"{date.Year}年{date.Month}月{date.Day}日\"], td.ant-picker-cell-in-view[title=\"{date:yyyy年MM月dd日}\"]").First;
        for (var i = 0; i < 4 && await cell.CountAsync() == 0; i++)
        {
            var before = await header.InnerTextAsync();
            var direction = date < DateTime.Today ? ".ant-picker-header-prev-btn" : ".ant-picker-header-next-btn";
            await dropdown.Locator(direction).ClickAsync();
            await Expect(header).Not.ToHaveTextAsync(before);
        }

        await cell.ClickAsync();
        await Expect(input).ToHaveValueAsync(new Regex($"{date.Year}年0?{date.Month}月0?{date.Day}日"));
    }

    /// <summary>按下確認框裡的按鈕（例如「刪除」「開始產生」）。</summary>
    /// <remarks>只找 AntDesign 確認框（.ant-modal-confirm），避免點到底下設定視窗裡同名的按鈕。</remarks>
    protected static async Task ConfirmAsync(IPage page, string okText)
        => await page.Locator(".ant-modal-confirm .ant-btn")
            .Filter(new() { HasTextRegex = SpacedText(okText) })
            .Last.ClickAsync();

    private static Regex SpacedText(string text)
        => new($@"^\s*{string.Join(@"\s*", text.ToCharArray().Select(c => Regex.Escape(c.ToString())))}\s*$");

    /// <summary>可見對話窗的 OK 按鈕（AntDesign 預設文字「確 定」）。</summary>
    protected static async Task ClickModalOkAsync(IPage page)
        => await page.Locator(".ant-modal-wrap:visible .ant-modal-footer .ant-btn-primary").Last.ClickAsync();

    /// <summary>執行會觸發下載的動作，回傳下載的檔名。</summary>
    protected static async Task<string> DownloadAsync(IPage page, Func<Task> action)
    {
        var download = await page.RunAndWaitForDownloadAsync(action, new() { Timeout = 60_000 });
        // 等檔案真的寫完再結束：context 關掉時還有下載在進行，瀏覽器行程可能整個結束。
        var path = Path.Combine(Path.GetTempPath(), "MeetingRecordE2E-downloads", Guid.NewGuid().ToString("N") + "-" + download.SuggestedFilename);
        await download.SaveAsAsync(path);
        Assert.True(new FileInfo(path).Length > 0, $"下載的檔案「{download.SuggestedFilename}」是空的");
        return download.SuggestedFilename;
    }

    /// <summary>產生一個假的影音檔（隨機位元組；E2E 環境的假轉檔不讀內容）。</summary>
    protected string FakeMediaFile(string name, int size = 64 * 1024)
    {
        var path = Path.Combine(App.WorkPath, name);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>在會議紀錄頁開「新增」並填好標題、選好影音檔（還沒按確定），回傳標題與本機檔案路徑。</summary>
    protected async Task<(string Title, string Media)> FillNewMeetingWithMediaAsync(IPage page, int size = 64 * 1024)
    {
        await GotoAsync(page, "/meetings", "會議標題");
        var title = Unique("E2E 上傳會議");
        var media = FakeMediaFile($"{title}.mp3", size);

        await page.GetByRole(AriaRole.Button, new() { Name = "新增" }).First.ClickAsync();
        await page.GetByPlaceholder("請輸入會議標題").FillAsync(title);
        await page.Locator(".ant-modal-wrap:visible input[type=file]").SetInputFilesAsync(media);
        await Expect(page.GetByText("待上傳：")).ToBeVisibleAsync();
        return (title, media);
    }

    /// <summary>按確定並在費用確認框按「儲存並開始轉錄」，等清單出現這一列（上傳在背景進行）。</summary>
    protected static async Task SaveNewMeetingWithMediaAsync(IPage page, string title)
    {
        await ClickModalOkAsync(page);
        await ConfirmAsync(page, "儲存並開始轉錄");
        await Expect(Row(page, title)).ToBeVisibleAsync();
    }

    /// <summary>新增一筆帶影音檔的會議（走分塊上傳），回傳標題。</summary>
    protected async Task<string> AddMeetingWithMediaAsync(IPage page)
    {
        var (title, _) = await FillNewMeetingWithMediaAsync(page);
        await SaveNewMeetingWithMediaAsync(page, title);
        return title;
    }

    /// <summary>等某一列出現指定狀態文字；清單靠背景事件重載，保險起見每隔幾秒按一次「重新整理」。</summary>
    protected static async Task WaitForRowTextAsync(IPage page, string title, string text, int? column = null, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var target = column is null ? Row(page, title) : Row(page, title).Locator($"td:nth-child({column})");
        while (!Regex.IsMatch(await target.InnerTextAsync(), Regex.Escape(text)))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"「{title}」等了 {timeoutSeconds} 秒還沒出現「{text}」，目前內容：{await Row(page, title).InnerTextAsync()}");
            }

            await page.WaitForTimeoutAsync(2000);
            if (await page.Locator(".ant-modal-wrap:visible").CountAsync() == 0)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "重新整理" }).ClickAsync();
            }
        }
    }

    /// <summary>右下角背景工作面板裡，某場會議的那一列。</summary>
    protected static ILocator JobPanelRow(IPage page, string meetingTitle)
        => page.Locator(".job-progress-panel-row").Filter(new() { HasText = meetingTitle });

    /// <summary>
    /// 讓這個瀏覽器的每個上傳分塊晚 <paramref name="delay"/> 才送出（L4）：本機上傳太快，不放慢就看不到「上傳中」、也來不及按取消。
    /// 只影響 <c>/api/v1/uploads/</c>，Blazor 連線不受影響。
    /// </summary>
    protected static Task ThrottleUploadsAsync(IPage page, TimeSpan delay)
        => page.RouteAsync("**/api/v1/uploads/**", async route =>
        {
            await Task.Delay(delay);
            await route.ContinueAsync();
        });

    protected static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";
}
