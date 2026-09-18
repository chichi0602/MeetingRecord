using AntDesign;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 左邊改、右邊看的 Markdown 編修視窗，附常駐的搜尋／取代列。
///
/// <para>
/// 放在 <c>Commons</c> 而不是 <c>Views/Meetings</c>：它不知道「會議」是什麼，
/// 存檔與匯出都交回呼叫端做（見 <see cref="OnSave"/>、<see cref="OnExport"/>），
/// 所以日後拿去編修專案說明或提示詞範本都可以直接用。
/// </para>
///
/// <para>
/// ⚠️ 元件不注入任何 Business 服務。<c>Components/Commons</c> 底下的元件目前都是純 UI
/// （<c>FormModalHelper</c>、<c>FormKeyboardHelper</c>、<c>FileDropZone</c>），
/// 在這裡開第一個相依會讓整個共用目錄跟著綁上 Business。
/// </para>
/// </summary>
public partial class MarkdownEditorModal : IDisposable
{
    /// <summary>
    /// 右邊預覽的重繪延遲。
    ///
    /// <para>
    /// ⚠️ 這個 debounce 是套在**預覽**上，不是套在文字繫結上——兩者刻意分開。
    /// 繫結必須即時（搜尋、取代、存檔都要讀到當下的文字，0.4.52 在 AI 問答上踩過
    /// 「讀到舊值」那個坑）；預覽則是純顯示，慢 250ms 沒有任何正確性問題。
    /// </para>
    ///
    /// <para>
    /// 為什麼一定要 debounce：<c>@((MarkupString)previewHtml)</c> 在 render tree 裡是
    /// 單一 markup frame，字串一變就是**整串重送**，不是局部更新。8KB 的 Markdown 會產出
    /// 12KB 左右的 HTML，不 debounce 的話每秒打 8 個字就是每秒往下推 100KB。
    /// 250ms：小於 150ms 省不到什麼（人的自然停頓多半超過 200ms），
    /// 大於 400ms 開始感覺遲鈍。
    /// </para>
    /// </summary>
    private const int PreviewDebounceMilliseconds = 250;

    /// <summary>訊息區再矮也要看得到內容。</summary>
    private const string MarkdownEditorBaseClass = "markdown-editor-modal";

    [Inject] private TextEditorInterop TextEditor { get; set; } = default!;

    [Inject] private ModalService ModalService { get; set; } = default!;

    [Inject] private IMessageService MessageService { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }

    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    [Parameter] public string Title { get; set; } = "會議紀錄";

    /// <summary>
    /// 開啟時要載入的內容。
    ///
    /// <para>
    /// ⚠️ <b>單向，刻意不是 <c>@bind-Content</c>。</b>內部只在 <see cref="Visible"/>
    /// 由 false 變 true 的那一次吃進緩衝，之後父層再怎麼 re-render 都不會覆蓋。
    /// 這一點很重要：會議頁的進度通知器會週期性地 <c>ReloadAsync</c> → <c>StateHasChanged</c>，
    /// 雙向繫結的話使用者打到一半的內容會被資料庫版本洗掉。
    /// </para>
    /// </summary>
    [Parameter] public string? Content { get; set; }

    /// <summary>false 時只顯示右邊的預覽，沒有搜尋列、沒有編輯區、沒有儲存鈕。</summary>
    [Parameter] public bool CanEdit { get; set; }

    [Parameter] public bool CanExport { get; set; }

    [Parameter] public bool IsExporting { get; set; }

    /// <summary>
    /// 下載 PDF。參數是**編輯區當下**的內容，不是資料庫裡那份。
    ///
    /// <para>
    /// ⚠️ 這裡刻意不是無參數的 EventCallback：使用者改了字、還沒儲存就按下載，
    /// 拿舊內容去產 PDF 會給出一份與畫面不符的檔案，而且完全沒有徵兆。
    /// </para>
    /// </summary>
    [Parameter] public EventCallback<string> OnExport { get; set; }

    [Parameter] public bool IsSaving { get; set; }

    /// <summary>
    /// 儲存。參數是編修後的內容。
    ///
    /// <para>
    /// ⚠️ 元件刻意**不自己呼叫** <c>MeetingService.UpdateDraftAsync</c>：存檔之後要做什麼
    /// 兩個呼叫端不一樣（會議紀錄頁重載清單、專案頁重載專案脈絡），而且匯出 PDF 的
    /// <c>MeetingDocumentExporter.BuildHtml</c> 在兩邊的引數也不同（專案頁多帶專案名稱）。
    /// 搬進來會靜默弄丟那個參數。
    /// </para>
    /// </summary>
    [Parameter] public EventCallback<string> OnSave { get; set; }

    /// <summary>
    /// 視窗底部的一行說明。用來講「為什麼這裡不能改」——
    /// 已歸屬專案的會議在會議紀錄頁是唯讀的，沒有這行等於功能無故消失。
    /// </summary>
    [Parameter] public string? Notice { get; set; }

    private ElementReference sourceElement;

    private bool wasVisible;

    private string editingText = string.Empty;

    private string originalText = string.Empty;

    private string previewHtml = string.Empty;

    private string searchTerm = string.Empty;

    private string replaceTerm = string.Empty;

    private IReadOnlyList<TextMatch> matches = [];

    private int currentIndex = -1;

    private CancellationTokenSource? previewCts;

    /// <summary>
    /// 編修時用最大的一級（左右分欄需要寬度），唯讀時只有一欄文章，880 就夠了——
    /// 一行太長反而難讀。
    /// </summary>
    private string ModalClass =>
        CanEdit
            ? $"{MarkdownEditorBaseClass} meeting-view-modal"
            : $"{MarkdownEditorBaseClass} form-modal-standard";

    /// <summary>
    /// 「第 n / 共 m 筆」。
    ///
    /// <para>
    /// ⚠️ 還沒按過「下一個」時（<c>currentIndex</c> 是 -1）只報總數，不要寫「第 0 筆」——
    /// 打字當下刻意不移動游標（邊打邊跳會讓畫面亂捲），此時並沒有「當前這一筆」。
    /// </para>
    /// </summary>
    private string MatchCountText
    {
        get
        {
            if (matches.Count == 0)
            {
                return string.IsNullOrEmpty(searchTerm) ? string.Empty : "沒有符合";
            }

            return currentIndex < 0
                ? $"共 {matches.Count} 筆"
                : $"第 {currentIndex + 1} / 共 {matches.Count} 筆";
        }
    }

    /// <summary>搜尋字串。改動時只重算筆數，不移動游標——邊打邊跳會讓畫面瘋狂亂捲。</summary>
    private string SearchTerm
    {
        get => searchTerm;
        set
        {
            if (searchTerm == value)
            {
                return;
            }

            searchTerm = value ?? string.Empty;
            RecomputeMatches(resetIndex: true);
        }
    }

    public void Dispose()
    {
        // ⚠️ 視窗關掉時可能還有一個 250ms 的 Task.Delay 在飛，
        // 它醒來會對已經處置的元件呼叫 InvokeAsync。
        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = null;

        GC.SuppressFinalize(this);
    }

    protected override void OnParametersSet()
    {
        // ⚠️ 只在 false → true 的那一次吃進來。見 Content 的說明。
        if (Visible && !wasVisible)
        {
            ApplyText(TextSearchHelper.NormalizeNewLines(Content), recomputeMatches: false);
            originalText = editingText;

            searchTerm = string.Empty;
            replaceTerm = string.Empty;
            matches = [];
            currentIndex = -1;

            RenderPreviewNow();
        }

        wasVisible = Visible;
    }

    /// <summary>
    /// <b><see cref="editingText"/> 唯一的寫入點。</b>
    ///
    /// <para>
    /// ⚠️ 不要在別的地方直接指派。每一次文字改變都會讓 <see cref="matches"/> 的索引失效，
    /// 集中在這裡才能保證「重算 + clamp」不會被漏掉。漏掉的話畫面會出現
    /// 「第 8 / 共 3 筆」，而且下一次「下一個」會索引越界。
    /// </para>
    /// </summary>
    private void ApplyText(string next, bool recomputeMatches = true)
    {
        editingText = next ?? string.Empty;

        if (recomputeMatches)
        {
            RecomputeMatches(resetIndex: false);
        }
    }

    private void RecomputeMatches(bool resetIndex)
    {
        matches = TextSearchHelper.FindAll(editingText, searchTerm);

        if (matches.Count == 0)
        {
            currentIndex = -1;
            return;
        }

        currentIndex = resetIndex
            ? -1
            : Math.Min(currentIndex, matches.Count - 1);
    }

    private async Task OnSourceInputAsync(ChangeEventArgs args)
    {
        ApplyText(args.Value?.ToString() ?? string.Empty);
        await SchedulePreviewAsync();
    }

    /// <summary>Enter＝下一筆、Shift+Enter＝上一筆。兩者都必須擋掉輸入法組字中的 Enter。</summary>
    private async Task OnSearchKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnFindNextAsync();
            return;
        }

        if (FormKeyboardHelper.IsReverseSubmit(args))
        {
            await OnFindPreviousAsync();
        }
    }

    private Task OnFindNextAsync()
    {
        currentIndex = TextSearchHelper.NextIndex(currentIndex, matches.Count);
        return HighlightCurrentAsync();
    }

    private Task OnFindPreviousAsync()
    {
        currentIndex = TextSearchHelper.PreviousIndex(currentIndex, matches.Count);
        return HighlightCurrentAsync();
    }

    private async Task HighlightCurrentAsync()
    {
        if (currentIndex < 0 || currentIndex >= matches.Count)
        {
            return;
        }

        var match = matches[currentIndex];
        await TextEditor.SelectRangeAsync(sourceElement, match.Start, match.Length);
    }

    /// <summary>取代目前這一筆。取代之後索引全部失效，所以重算並停在同一筆的位置上。</summary>
    private async Task OnReplaceCurrentAsync()
    {
        if (currentIndex < 0 || currentIndex >= matches.Count)
        {
            return;
        }

        var replacedAt = currentIndex;
        var (next, caret) = TextSearchHelper.ReplaceAt(editingText, matches, currentIndex, replaceTerm);

        ApplyText(next);

        // 取代掉一筆之後總數少一；停在原本那個序號上，於是「下一個」會走到真正的下一筆。
        currentIndex = matches.Count == 0 ? -1 : Math.Min(replacedAt, matches.Count - 1);

        await SchedulePreviewAsync();
        await TextEditor.SelectRangeAsync(sourceElement, caret, 0);
    }

    /// <summary>
    /// 全部取代。
    ///
    /// <para>
    /// 刻意**把畫面留在原地**：全部取代是一次批次操作，把游標丟到「最後一筆被取代處」
    /// 聽起來貼心，實際上會把使用者突然捲到文件末尾、失去位置感。
    /// <c>ShiftCaret</c> 讓游標仍然指著取代前同一個語意位置。
    /// </para>
    /// </summary>
    private async Task OnReplaceAllAsync()
    {
        if (matches.Count == 0)
        {
            return;
        }

        var before = await TextEditor.GetStateAsync(sourceElement);
        var caretAfter = TextSearchHelper.ShiftCaret(
            matches,
            before.Caret,
            searchTerm.Length,
            (replaceTerm ?? string.Empty).Length);

        var (next, count) = TextSearchHelper.ReplaceAll(editingText, searchTerm, replaceTerm);

        ApplyText(next);

        // 舊索引全部失效。重算之後通常是 0 筆，除非取代字串本身還含著搜尋字串。
        currentIndex = -1;

        await SchedulePreviewAsync();
        await TextEditor.SelectRangeAsync(sourceElement, caretAfter, 0);
        await TextEditor.SetScrollTopAsync(sourceElement, before.ScrollTop);
        await MessageService.SuccessAsync($"已取代 {count} 筆。");
    }

    private async Task SchedulePreviewAsync()
    {
        // trailing debounce：連續打字期間一直取消前一次排程，停下來才真的渲染。
        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = new CancellationTokenSource();

        var token = previewCts.Token;

        try
        {
            await Task.Delay(PreviewDebounceMilliseconds, token);

            RenderPreviewNow();
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // 正常路徑：使用者還在打字。
        }
        catch (ObjectDisposedException)
        {
            // 視窗在等待期間被關掉了。
        }
    }

    /// <summary>⚠️ 全站唯一的 Markdown 管線，不要在這裡另外開一條 Markdig pipeline。</summary>
    private void RenderPreviewNow() => previewHtml = MarkdownRenderer.ToHtml(editingText);

    private Task OnExportAsync() => OnExport.InvokeAsync(editingText);

    private async Task OnSaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        await OnSave.InvokeAsync(editingText);
    }

    private async Task OnCancelAsync()
    {
        // 唯讀、或根本沒改過，就直接關。
        if (CanEdit && !string.Equals(editingText, originalText, StringComparison.Ordinal))
        {
            // 0.4.84 起改用共用的 FormDirtyHelper——文案與這裡原本寫死的那一句一字不差
            // （有測試釘住），差別只在現在全站 10 個表單共用同一份說法。
            var discard = await FormDirtyHelper.ConfirmDiscardAsync(ModalService, "這份會議紀錄");

            if (!discard)
            {
                return;
            }
        }

        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        Visible = false;
        await VisibleChanged.InvokeAsync(false);
    }
}
