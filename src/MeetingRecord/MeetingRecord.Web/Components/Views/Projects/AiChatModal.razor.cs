using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Web.Services;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Views.Projects;

/// <summary>
/// AI 問答視窗。專案層級與單一會議共用同一個元件，差別只在 <see cref="Scope"/> 與 <see cref="TargetId"/>。
/// </summary>
public partial class AiChatModal : ComponentBase
{
    private const string PdfContentType = "application/pdf";

    [Inject]
    private AiChatService AiChatService { get; set; } = default!;

    [Inject]
    private IPdfRenderer PdfRenderer { get; set; } = default!;

    [Inject]
    private FileDownloadInterop FileDownloadInterop { get; set; } = default!;

    [Inject]
    private ClipboardInterop ClipboardInterop { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Inject]
    private ILogger<AiChatModal> Logger { get; set; } = default!;

    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public EventCallback<bool> VisibleChanged { get; set; }

    [Parameter]
    public AiChatScope Scope { get; set; }

    [Parameter]
    public int TargetId { get; set; }

    [Parameter]
    public string Title { get; set; } = "AI 問答";

    /// <summary>對象名稱（專案或會議標題），用在匯出的檔名與 PDF 表頭。</summary>
    [Parameter]
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// 是否顯示匯出 PDF 的按鈕。沿用清單頁既有的「匯出」動作權限，
    /// 免得同一個畫面上出現「不能匯出會議紀錄、卻能匯出 AI 答案」。
    /// </summary>
    [Parameter]
    public bool CanExport { get; set; }

    private readonly List<AiChatMessageItem> messages = [];

    private string question = string.Empty;
    private string streamingAnswer = string.Empty;
    private string? sourceNotice;
    private string? errorMessage;
    private bool isAsking;

    /// <summary>目前正在編輯第幾則；-1 表示沒有在編輯。</summary>
    private int editingIndex = -1;
    private string editingText = string.Empty;
    private bool isSavingEdit;
    private bool isRegenerating;

    /// <summary>正在匯出第幾則；-1 表示沒有。用來擋連點——每按一次都會起一個無頭瀏覽器。</summary>
    private int downloadingIndex = -1;
    private bool isDownloadingConversation;

    /// <summary>上一次載入歷史用的對象，用來判斷是否需要重新載入。</summary>
    private (AiChatScope Scope, int TargetId)? loadedTarget;

    /// <summary>
    /// 任一個耗時或會改狀態的動作進行中。所有按鈕共用同一個閘門：
    /// 匯出 PDF 要起無頭瀏覽器（數秒、上百 MB），連點會把伺服器打爛。
    /// </summary>
    private bool IsBusy =>
        isAsking || isSavingEdit || isRegenerating || isDownloadingConversation || downloadingIndex >= 0;

    protected override async Task OnParametersSetAsync()
    {
        // 視窗關閉時不做事；同一個對象重複設定參數也不要重載，否則每次重繪都打一次資料庫。
        if (!Visible || TargetId <= 0)
        {
            return;
        }

        if (loadedTarget == (Scope, TargetId))
        {
            return;
        }

        loadedTarget = (Scope, TargetId);
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        messages.Clear();
        streamingAnswer = string.Empty;
        sourceNotice = null;
        errorMessage = null;

        // ⚠️ 編輯狀態一定要跟著重設。editingIndex 指的是清單裡的第幾則，
        // 別人清空對話之後那個索引就不存在了，留著會讓編輯框停在空氣上。
        CancelEditState();

        try
        {
            messages.AddRange(await AiChatService.GetHistoryAsync(Scope, TargetId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI chat history failed. Scope={Scope}, TargetId={TargetId}", Scope, TargetId);
            errorMessage = $"載入對話歷史失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// Enter 送出、Shift+Enter 換行。
    /// 0.4.77 起改走 <see cref="FormKeyboardHelper"/>，補上中文輸入法組字的判斷——
    /// 先前只擋 Shift，使用者用注音打完問題按 Enter 選字時會直接送出半成品。
    /// </summary>
    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (!FormKeyboardHelper.IsSubmit(args))
        {
            return;
        }

        await OnAskAsync();
    }

    private async Task OnAskAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        var asked = question.Trim();
        question = string.Empty;
        streamingAnswer = string.Empty;
        errorMessage = null;
        isAsking = true;

        // 先把提問放進畫面，使用者才不會覺得按了沒反應。實際落庫在服務層與回答一起做。
        messages.Add(new AiChatMessageItem(AiChatService.UserRole, asked, null, DateTime.Now));
        StateHasChanged();

        try
        {
            var answer = await AiChatService.AskAsync(Scope, TargetId, asked, OnDelta);

            // 用服務層回傳的內容重建整段歷史，順便把提問者名稱與落庫時間補正。
            await LoadHistoryAsync();
            sourceNotice = DescribeSources(answer);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI chat failed. Scope={Scope}, TargetId={TargetId}", Scope, TargetId);
            errorMessage = $"回答失敗：{ex.Message}";

            // 失敗時把樂觀加入的那則提問收回來，避免畫面上留下一則沒有回應的問題。
            messages.RemoveAt(messages.Count - 1);
        }
        finally
        {
            isAsking = false;
            streamingAnswer = string.Empty;

            // 再清一次，不是多餘的：Enter 送出時，瀏覽器仍會執行 textarea 的預設行為
            // 插入一個換行，而 AntDesign 元件的 OnkeyDown 是 EventCallback，
            // 拿不到 preventDefault（那需要 @onkeydown:preventDefault，只能用在原生元素）。
            // 那個換行會在上面清空之後才透過 input 事件寫回來，所以要在結束時再抹掉。
            question = string.Empty;

            StateHasChanged();
        }
    }

    /// <summary>
    /// 串流回呼由 HTTP 讀取的執行緒觸發，必須切回 UI 執行緒才能碰元件狀態。
    /// </summary>
    private void OnDelta(string delta)
        => _ = InvokeAsync(() =>
        {
            streamingAnswer += delta;
            StateHasChanged();
        });

    private static string DescribeSources(AiChatAnswer answer)
    {
        var parts = new List<string> { $"本次讀取了 {answer.UsedLabels.Count} 份資料" };

        // 被截斷或跳過一定要講出來，否則使用者會以為 AI 看過了全部內容。
        if (answer.TruncatedLabels.Count > 0)
        {
            parts.Add($"內容過長已截斷：{string.Join("、", answer.TruncatedLabels)}");
        }

        if (answer.SkippedLabels.Count > 0)
        {
            parts.Add($"無法讀取（掃描檔、不支援的格式或尚無內容）：{string.Join("、", answer.SkippedLabels)}");
        }

        return string.Join("；", parts) + "。";
    }

    #region 複製

    private async Task OnCopyAsync(int index)
    {
        if (IsBusy || index < 0 || index >= messages.Count)
        {
            return;
        }

        errorMessage = null;

        // 複製 Markdown 原文而不是渲染後的文字——貼到別的地方還留得住格式。
        var copied = await ClipboardInterop.CopyTextAsync(messages[index].Content);

        if (copied)
        {
            _ = MessageService.SuccessAsync("已複製到剪貼簿");
        }
        else
        {
            // 瀏覽器擋掉時要講清楚，不要假裝成功——Firefox 與非 https 的區網部署都會走到這裡。
            errorMessage = "瀏覽器不允許這次複製（可能是安全性限制）。請直接選取訊息內容後按 Ctrl+C。";
        }
    }

    #endregion

    #region 編輯與重新產生

    private void OnStartEdit(int index)
    {
        if (IsBusy || index < 0 || index >= messages.Count)
        {
            return;
        }

        errorMessage = null;
        editingIndex = index;
        editingText = messages[index].Content;
    }

    private void OnCancelEdit() => CancelEditState();

    private void CancelEditState()
    {
        editingIndex = -1;
        editingText = string.Empty;
    }

    /// <summary>只更正文字，不呼叫模型，所以不需要費用確認。</summary>
    private async Task OnSaveEditAsync()
    {
        if (IsBusy || editingIndex < 0 || editingIndex >= messages.Count || string.IsNullOrWhiteSpace(editingText))
        {
            return;
        }

        var index = editingIndex;
        var original = messages[index];
        var newContent = editingText.Trim();

        if (string.Equals(original.Content, newContent, StringComparison.Ordinal))
        {
            CancelEditState();
            return;
        }

        isSavingEdit = true;
        errorMessage = null;

        try
        {
            var outcome = await AiChatService.UpdateMessageAsync(Scope, TargetId, index, original, newContent);
            if (!HandleUpdateOutcome(outcome))
            {
                return;
            }

            await LoadHistoryAsync();
            _ = MessageService.SuccessAsync("已更新這則訊息");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Updating AI chat message failed. Scope={Scope}, TargetId={TargetId}, Index={Index}",
                Scope, TargetId, index);
            errorMessage = $"更新失敗：{ex.Message}";
        }
        finally
        {
            isSavingEdit = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 改寫提問並重新產生答案。<b>會呼叫模型、會產生費用</b>，所以一定先跳二次確認（§6.3）。
    /// </summary>
    private async Task OnRegenerateAsync()
    {
        if (IsBusy || editingIndex < 0 || editingIndex >= messages.Count || string.IsNullOrWhiteSpace(editingText))
        {
            return;
        }

        var index = editingIndex;
        var original = messages[index];
        if (!original.IsUser)
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認重新產生答案（會產生費用）",
            Content = "這會以改過的問題重新呼叫 Azure OpenAI，"
                + "並以新答案覆蓋下方原本的回答（包含人工編修過的部分），且無法復原。確定要繼續嗎？",
            OkText = "覆蓋並重新產生",
            CancelText = "取消",
            MaskClosable = false,
            OkButtonProps = new ButtonProps { Danger = true },
        });

        if (!confirmed)
        {
            Logger.LogDebug(
                "AI chat regeneration cancelled by user. Scope={Scope}, TargetId={TargetId}, Index={Index}",
                Scope, TargetId, index);
            return;
        }

        var newQuestion = editingText.Trim();

        isRegenerating = true;
        streamingAnswer = string.Empty;
        errorMessage = null;
        CancelEditState();
        StateHasChanged();

        try
        {
            var answer = await AiChatService.RegenerateAsync(
                Scope, TargetId, index, original.Content, newQuestion, OnDelta);

            await LoadHistoryAsync();
            sourceNotice = DescribeSources(answer);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Regenerating AI chat answer failed. Scope={Scope}, TargetId={TargetId}, Index={Index}",
                Scope, TargetId, index);
            errorMessage = $"重新產生失敗：{ex.Message}";
        }
        finally
        {
            isRegenerating = false;
            streamingAnswer = string.Empty;
            StateHasChanged();
        }
    }

    /// <summary>把 store 的結果翻成畫面訊息。回傳是否可以繼續。</summary>
    private bool HandleUpdateOutcome(UpdateOutcome outcome)
    {
        switch (outcome)
        {
            case UpdateOutcome.Updated:
                return true;

            case UpdateOutcome.NotFound:
                errorMessage = "這段對話已經被清空，剛才的修改沒有寫入。";
                return false;

            default:
                // 對話是同專案／會議底下所有人共用的，別人先改過就會走到這裡。
                errorMessage = "這則訊息已被其他人更動，請重新開啟視窗後再試一次。";
                return false;
        }
    }

    #endregion

    #region 匯出 PDF

    private async Task OnDownloadMessageAsync(int index)
    {
        if (IsBusy || index < 0 || index >= messages.Count)
        {
            return;
        }

        downloadingIndex = index;
        errorMessage = null;
        StateHasChanged();

        try
        {
            var exportedAt = DateTime.Now;
            var html = AiChatDocumentExporter.BuildMessageHtml(TargetName, messages[index], index + 1, exportedAt);
            var fileName = AiChatDocumentExporter.BuildMessageFileName(TargetName, index + 1, exportedAt);

            await ExportAsync(html, fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Exporting AI chat message failed. Scope={Scope}, TargetId={TargetId}, Index={Index}",
                Scope, TargetId, index);
            errorMessage = $"匯出 PDF 失敗：{ex.Message}";
        }
        finally
        {
            downloadingIndex = -1;
            StateHasChanged();
        }
    }

    private async Task OnDownloadConversationAsync()
    {
        if (IsBusy || messages.Count == 0)
        {
            return;
        }

        isDownloadingConversation = true;
        errorMessage = null;
        StateHasChanged();

        try
        {
            var exportedAt = DateTime.Now;
            var html = AiChatDocumentExporter.BuildConversationHtml(TargetName, messages, exportedAt);
            var fileName = AiChatDocumentExporter.BuildConversationFileName(TargetName, exportedAt);

            await ExportAsync(html, fileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Exporting AI chat conversation failed. Scope={Scope}, TargetId={TargetId}",
                Scope, TargetId);
            errorMessage = $"匯出 PDF 失敗：{ex.Message}";
        }
        finally
        {
            isDownloadingConversation = false;
            StateHasChanged();
        }
    }

    private async Task ExportAsync(string html, string fileName)
    {
        var pdf = await PdfRenderer.RenderAsync(html);

        await FileDownloadInterop.SaveBytesAsync(fileName, pdf, PdfContentType);
    }

    #endregion

    private async Task OnClearAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // 這會直接把對話檔刪掉，不可復原，而且對話是所有人共用的。
        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認清空這段對話",
            Content = $"將刪除這段對話的全部 {messages.Count} 則訊息，所有人都會看不到，且無法復原。確定要清空嗎？",
            OkText = "清空",
            CancelText = "取消",
            MaskClosable = false,
            OkButtonProps = new ButtonProps { Danger = true },
        });

        if (!confirmed)
        {
            return;
        }

        try
        {
            await AiChatService.ClearHistoryAsync(Scope, TargetId);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Clearing AI chat history failed. Scope={Scope}, TargetId={TargetId}", Scope, TargetId);
            errorMessage = $"清空對話失敗：{ex.Message}";
        }
    }

    private async Task OnCancelAsync()
    {
        // 讓下次開啟時重新載入（可能是另一個對象，或期間有人問了新問題）。
        loadedTarget = null;
        CancelEditState();
        await VisibleChanged.InvokeAsync(false);
    }
}
