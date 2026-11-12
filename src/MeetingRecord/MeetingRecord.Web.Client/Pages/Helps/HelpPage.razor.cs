using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MeetingRecord.Dtos.Helps;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.Helps;

/// <summary>
/// 使用說明頁（1.0.42 起）。
///
/// <para>
/// 內容<b>不寫在這裡</b>，而是 <c>docs/guides/系統使用說明.md</c> 那一份：伺服器把它嵌在 Web 組件裡，
/// 經 <c>GET api/v1/help/manual</c> 給這頁，這裡用同一套 <see cref="HelpManual"/> 切分類與搜尋。
/// 「文件庫的使用說明」與「系統裡的使用說明」永遠是同一份內容。
/// </para>
///
/// <para>
/// 問 AI 助理走 <c>POST api/v1/help/assistant/ask</c>（串流）。要不要送「管理員」章節、稽核都在伺服器；
/// 「停止」＝中斷請求，伺服器跟著停止呼叫 AI。
/// </para>
/// </summary>
public partial class HelpPage : ComponentBase, IAsyncDisposable
{
    /// <summary>解析一次（開頁時），之後切分類、搜尋都不重新解析。</summary>
    private HelpManual? manual;
    private string? loadError;
    private string RoleMessage = string.Empty;

    private string selectedCategory = HelpManual.CategoryNames[0];
    private string searchText = string.Empty;
    private IReadOnlyList<HelpTopic> searchResults = [];

    private ElementReference rootElement;
    private DotNetObjectReference<HelpPage>? selfReference;
    private bool isAttached;

    /// <summary>重繪後要做的前端動作：高亮、捲到錨點、捲回頂端。</summary>
    private bool pendingHighlight;
    private string? pendingScrollId;
    private bool pendingScrollTop;

    /// <summary>「問 AI 助理」的問答串（1.0.50）：新的問答自動捲到最下方。</summary>
    private ElementReference assistantThread;

    /// <summary>下一次畫完要把問答串強制捲到底（發問時）。</summary>
    private bool pendingAssistantScroll;

    [Inject] private HelpViewInterop HelpViewInterop { get; set; } = default!;
    [Inject] private HelpApi HelpApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private ILogger<HelpPage> Logger { get; set; } = default!;

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
            .Select(x => new HelpAssistantTurnDto { Question = x.Question, Answer = x.Answer })
            .ToList();

        var turn = new AssistantTurn(question);
        assistantTurns.Add(turn);
        assistantQuestion = string.Empty;
        isAsking = true;
        pendingAssistantScroll = true;
        askCts = new CancellationTokenSource();
        var token = askCts.Token;
        StateHasChanged();

        try
        {
            var (answer, _) = await HelpApi.AskAsync(
                question,
                history,
                assistantThreadKey,
                delta =>
                {
                    turn.Answer += delta;
                    StateHasChanged();
                },
                token);

            turn.Answer = answer;
            turn.AnswerHtml = MarkdownRenderer.ToHtml(answer);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 使用者按「停止」：請求中斷，伺服器停止呼叫 AI（已送出的照樣記帳，記成 Cancelled）；畫面留下已收到的部分。
            turn.AnswerHtml = MarkdownRenderer.ToHtml(turn.Answer + "\n\n（已停止）");
        }
        catch (Exception ex)
        {
            // 訊息是伺服器給的原文（額度用完、說明太長等，見 HelpAssistantFailure）；稽核也由伺服器寫。
            Logger.LogWarning(ex, "Help assistant failed.");
            turn.Error = ex is InvalidOperationException ? ex.Message : $"AI 助理沒有回答成功：{ex.Message}";
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

        if (!disposed)
        {
            StateHasChanged();
        }
    }

    private void OnStopAssistant() => askCts?.Cancel();

    private void OnClearAssistant() => assistantTurns.Clear();

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
        // 離開頁面就停掉還在跑的回答（中斷請求），不要讓沒人看的答案繼續花錢。
        disposed = true;
        askCts?.Cancel();

        if (isAttached)
        {
            await HelpViewInterop.DetachAsync(rootElement);
        }

        selfReference?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        if (!PermissionView.CanAccessPage(MagicObjectHelper.角色_使用說明))
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/help");
            Logger.LogWarning("Help page denied because current user has not this role permission.");
            return;
        }

        var result = await HelpApi.GetManualAsync();
        if (!result.Success || result.Data is not { } dto)
        {
            loadError = result.ErrorMessage ?? "使用說明載入失敗，請聯絡系統管理員。";
            return;
        }

        var watch = Stopwatch.StartNew();
        manual = HelpManual.Parse(dto.Markdown);
        Logger.LogInformation("Help manual parsed. ElapsedMs={ElapsedMs}, Topics={Topics}", watch.ElapsedMilliseconds, manual.Topics.Count);

        assistantAvailable = dto.AssistantAvailable;
        assistantUnavailableReason = dto.AssistantUnavailableReason;
    }
}
