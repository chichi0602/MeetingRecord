using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.HelpAssistant;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Helps;

/// <summary>
/// 使用說明頁。
///
/// <para>
/// 內容<b>不寫在這裡</b>，而是 <c>docs/guides/系統使用說明.md</c> 那一份——
/// 它在建置時以內嵌資源的形式被放進組件（見 <c>MeetingRecord.Web.csproj</c>）。
/// 這樣「文件庫的使用說明」與「系統裡的使用說明」永遠是同一份內容，
/// 不會出現改了一邊忘了另一邊的情況。
/// </para>
///
/// <para>
/// ⚠️ 刻意<b>不</b>從檔案系統讀 <c>docs/</c>：那個目錄不會被發佈到輸出目錄，
/// 會變成「開發時看得到、上線後 404」。
/// </para>
/// </summary>
public partial class HelpView : ComponentBase, IAsyncDisposable
{
    /// <summary>
    /// 內嵌資源的名稱。與 csproj 的 <c>LogicalName</c> 必須一致——
    /// 改了一邊就會在執行期變成「載入中…」永遠不消失。
    /// </summary>
    private const string ResourceName = "MeetingRecord.Web.系統使用說明.md";

    /// <summary>解析一次（每個連線一次），之後切分類、搜尋都不重新解析。</summary>
    private HelpManual? manual;
    private string? loadError;
    private string RoleMessage = string.Empty;

    private string selectedCategory = HelpManual.CategoryNames[0];
    private string searchText = string.Empty;
    private IReadOnlyList<HelpTopic> searchResults = [];

    private ElementReference rootElement;
    private DotNetObjectReference<HelpView>? selfReference;
    private bool isAttached;

    /// <summary>重繪後要做的前端動作：高亮、捲到錨點、捲回頂端。</summary>
    private bool pendingHighlight;
    private string? pendingScrollId;
    private bool pendingScrollTop;

    /// <summary>「問 AI 助理」的問答串（1.0.50）：新的問答自動捲到最下方。</summary>
    private ElementReference assistantThread;

    /// <summary>下一次畫完要把問答串強制捲到底（發問時）。</summary>
    private bool pendingAssistantScroll;

    [Inject]
    public HelpViewInterop HelpViewInterop { get; set; } = default!;

    [Inject]
    public HelpAssistantService HelpAssistantService { get; set; } = default!;

    [Inject]
    public IAuditLogService AuditLogService { get; set; } = default!;

    #region 問 AI 助理（1.0.43）

    /// <summary>畫面上的一輪；答案串流中逐段累加，完成後渲染成 HTML。</summary>
    private sealed class AssistantTurn(string question)
    {
        public string Question { get; } = question;

        public string Answer { get; set; } = string.Empty;

        public string AnswerHtml { get; set; } = string.Empty;

        public string? Error { get; set; }

        public bool IsDone { get; set; }
    }

    private readonly List<AssistantTurn> assistantTurns = [];
    private string assistantQuestion = string.Empty;
    private bool assistantAvailable;
    private string assistantUnavailableReason = string.Empty;
    private bool isAsking;
    private CancellationTokenSource? askCts;
    private bool disposed;

    /// <summary>同一頁上的連續提問在 AI 對話紀錄裡串成一串。</summary>
    private readonly string assistantThreadKey = $"help:{Guid.NewGuid():N}";

    /// <summary>Enter 送出、Shift+Enter 換行；注音選字的 Enter 不送出（FormKeyboardHelper）。</summary>
    private async Task OnAssistantKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnAskAssistantAsync();
        }
    }

    /// <summary>
    /// 輸入框回寫（1.0.49）。發問中一律忽略：按 Enter 送出時瀏覽器仍會插入換行並送來 input 事件
    /// （AntDesign TextArea 的 OnkeyDown 擋不掉），照單全收的話問完輸入框又會出現「舊問題＋換行」。
    /// </summary>
    private void OnAssistantQuestionChanged(string? value)
    {
        if (isAsking)
        {
            return;
        }

        assistantQuestion = value ?? string.Empty;
    }

    private async Task OnAskAssistantAsync()
    {
        var question = assistantQuestion.Trim();
        if (isAsking || manual is null || !assistantAvailable || string.IsNullOrEmpty(question))
        {
            return;
        }

        // 只帶已經成功回答的輪次當上下文：失敗或停止的那一輪沒有完整答案，送出去只會誤導。
        var history = assistantTurns
            .Where(x => x.IsDone && x.Error is null)
            .Select(x => new HelpAssistantTurn(x.Question, x.Answer))
            .ToList();

        var turn = new AssistantTurn(question);
        assistantTurns.Add(turn);
        assistantQuestion = string.Empty;
        isAsking = true;
        pendingAssistantScroll = true;
        askCts = new CancellationTokenSource();
        var token = askCts.Token;
        StateHasChanged();

        var isAdmin = AuthenticationStateHelper.CheckIsAdmin();
        string? model = null;
        string outcome;

        try
        {
            var answer = await HelpAssistantService.AskAsync(
                manual.BuildAssistantContext(includeAdmin: isAdmin),
                history,
                question,
                delta => _ = InvokeAsync(() =>
                {
                    turn.Answer += delta;
                    StateHasChanged();
                }),
                token,
                assistantThreadKey);

            turn.Answer = answer.Content;
            turn.AnswerHtml = MarkdownRenderer.ToHtml(answer.Content);
            model = answer.ModelName;
            outcome = "成功";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者按「停止」：已經送出的照樣記帳（gateway 記成 Cancelled），畫面留下已收到的部分。
            turn.AnswerHtml = MarkdownRenderer.ToHtml(turn.Answer + "\n\n（已停止）");
            outcome = "停止";
        }
        catch (Exception ex)
        {
            if (ex is AiQuotaExceededException)
            {
                Logger.LogWarning("Help assistant blocked by AI quota.");
            }
            else
            {
                Logger.LogError(ex, "Help assistant failed.");
            }

            turn.Error = DescribeAssistantFailure(ex);
            outcome = $"失敗：{ex.GetType().Name}";
        }
        finally
        {
            turn.IsDone = true;
            isAsking = false;

            // 保險：問完（成功、失敗、停止）輸入框一律是空的。
            assistantQuestion = string.Empty;
            askCts?.Dispose();
            askCts = null;
        }

        // 問題全文已經在 AI 對話紀錄裡，稽核只記長度與結果。
        await AuditLogService.WriteForCurrentUserAsync(
            AuditActions.HelpAiAsk,
            success: outcome == "成功",
            targetType: "Help",
            detail: $"questionChars={question.Length}; includeAdmin={isAdmin}; model={model ?? "-"}; outcome={outcome}");

        if (!disposed)
        {
            StateHasChanged();
        }
    }

    private void OnStopAssistant() => askCts?.Cancel();

    private void OnClearAssistant() => assistantTurns.Clear();

    private static string DescribeAssistantFailure(Exception ex)
    {
        if (ex is AiQuotaExceededException)
        {
            return ex.Message;
        }

        var message = ex.Message;
        if (message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase))
        {
            return "使用說明超過目前 AI 模型能讀的長度，請聯絡系統管理員。";
        }

        return $"AI 助理沒有回答成功：{message}";
    }

    #endregion

    private bool IsSearching => !string.IsNullOrWhiteSpace(searchText);

    private HelpCategory? SelectedCategory => manual?.Categories.FirstOrDefault(x => x.Name == selectedCategory);

    private string SearchText
    {
        get => searchText;
        set
        {
            searchText = value ?? string.Empty;
            searchResults = manual?.Search(searchText) ?? [];
            pendingHighlight = true;
        }
    }

    private Task SelectCategoryAsync(string name)
    {
        selectedCategory = name;
        searchText = string.Empty;
        searchResults = [];
        pendingScrollTop = true;
        return Task.CompletedTask;
    }

    /// <summary>在搜尋框按 Esc（js/help-view.js 呼叫）。</summary>
    [JSInvokable]
    public Task ClearSearchFromJs()
        => InvokeAsync(() =>
        {
            SearchText = string.Empty;
            StateHasChanged();
        });

    /// <summary>
    /// 點了說明內容裡的 #錨點（js/help-view.js 呼叫）。目標可能在別的分類，先切過去、清掉搜尋，重繪後再捲。
    /// </summary>
    [JSInvokable]
    public Task JumpToAnchorFromJs(string id)
        => InvokeAsync(() =>
        {
            var category = manual?.FindCategoryOfAnchor(id);
            if (category is not null)
            {
                selectedCategory = category;
            }

            searchText = string.Empty;
            searchResults = [];
            pendingScrollId = id;
            StateHasChanged();
        });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (manual is null)
        {
            return;
        }

        if (!isAttached)
        {
            selfReference ??= DotNetObjectReference.Create(this);
            await HelpViewInterop.AttachAsync(rootElement, selfReference);
            isAttached = true;
        }

        if (pendingHighlight)
        {
            pendingHighlight = false;
            await HelpViewInterop.HighlightAsync(rootElement, HelpManual.SplitTerms(searchText));
        }

        if (pendingScrollId is not null || pendingScrollTop)
        {
            var id = pendingScrollId;
            pendingScrollId = null;
            pendingScrollTop = false;
            await HelpViewInterop.ScrollToAsync(rootElement, id);
        }

        // 問答串只在有內容時才渲染，清除對話後會是新的元素；follow 在 JS 端每個元素只掛一次。
        if (assistantTurns.Count > 0 && assistantThread.Context is not null)
        {
            await HelpViewInterop.FollowChatAsync(assistantThread);
            if (pendingAssistantScroll)
            {
                pendingAssistantScroll = false;
                await HelpViewInterop.ChatToBottomAsync(assistantThread);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 離開頁面就停掉還在跑的回答，不要讓沒人看的答案繼續花錢。
        disposed = true;
        askCts?.Cancel();

        if (isAttached)
        {
            await HelpViewInterop.DetachAsync(rootElement);
        }

        selfReference?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<HelpView> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_使用說明) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/help");
            Logger.LogWarning("Help view denied because current user has not this role permission.");
            return;
        }

        var markdown = ReadEmbeddedManual();
        if (markdown is null)
        {
            loadError = "使用說明載入失敗，請聯絡系統管理員。";
            return;
        }

        manual = HelpManual.Parse(StripDocumentFrontMatter(markdown));

        assistantAvailable = HelpAssistantService.IsAvailable;
        assistantUnavailableReason = assistantAvailable
            ? string.Empty
            : $"AI 助理目前無法使用：{HelpAssistantService.UnavailableReason}";

        // 標記漏了或打錯不會壞畫面（歸到第一類），但會讓章節跑錯分類——留線索給維護者，測試也會擋。
        if (manual.UnknownMarkers.Count > 0)
        {
            Logger.LogWarning(
                "Help manual sections without a valid category marker: {Sections}",
                string.Join("、", manual.UnknownMarkers));
        }
    }

    /// <summary>
    /// 切掉文件開頭的維護資訊，只留正文。
    ///
    /// <para>
    /// `docs/` 的每份文件都有一段表頭（文件版本／文件狀態／現行系統版本／最後核對日期），
    /// 這一份還多一段「這個檔案同時是系統內說明頁的來源」的提醒。那些是寫給維護者看的，
    /// **對使用者只是雜訊**——所以保留在檔案裡（文件庫的慣例不破），但不顯示在畫面上。
    /// </para>
    ///
    /// <para>
    /// 切法是「第一條水平線之前全部丟掉」。找不到分隔線時原樣回傳，
    /// 寧可多顯示一段表頭，也不要因為格式改了就整頁空白。
    /// </para>
    ///
    /// <remarks>public 是為了讓測試看得到（寫這段時 Web 專案還沒有 InternalsVisibleTo；
    /// 0.4.122 起有了，但這個方法保持 public 不動）。</remarks>
    /// </summary>
    public static string StripDocumentFrontMatter(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                return string.Join('\n', lines.Skip(index + 1)).TrimStart('\n', '\r');
            }
        }

        return markdown;
    }

    /// <summary>
    /// 讀出內嵌的使用說明。
    ///
    /// <para>
    /// ⚠️ <b>一定要去掉 BOM。</b><c>docs/*.md</c> 一律是 UTF-8 含 BOM（CI 強制檢查），
    /// 不處理的話字串第一個字元會是 <c>﻿</c>，Markdig 會把第一行的
    /// <c># 標題</c> 當成普通段落，整頁的標題層級就跑掉了。
    /// <c>detectEncodingFromByteOrderMarks</c> 會幫我們吃掉它。
    /// </para>
    /// </summary>
    private string? ReadEmbeddedManual()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // 這是建置設定壞掉，不是使用者的問題——要留下線索，不要只顯示空白頁。
            Logger.LogError(
                "Embedded manual not found. ResourceName={ResourceName}, Available={Available}",
                ResourceName,
                string.Join("、", assembly.GetManifestResourceNames()));

            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
