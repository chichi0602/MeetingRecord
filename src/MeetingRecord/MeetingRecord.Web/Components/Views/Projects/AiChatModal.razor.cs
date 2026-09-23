using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
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
    private NotificationService NotificationService { get; set; } = default!;

    [Inject]
    private ILogger<AiChatModal> Logger { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    /// <summary>待送出的附件在畫面上的樣子。縮圖只給小圖——大圖轉成 data URL 會整份經過 SignalR 送到瀏覽器。</summary>
    private sealed record PendingView(PendingAttachment Item, bool IsImage, string? ThumbnailUrl);

    /// <summary>超過這個大小的圖片不做縮圖，只顯示圖示（一般截圖遠小於此）。</summary>
    private const long MaxThumbnailBytes = 2L * 1024 * 1024;

    private readonly List<PendingView> pendingAttachments = [];
    private ElementReference chatArea;
    private int attachInputKey;
    private bool isReadingAttachments;

    /// <summary>目前展開預覽的歷史附件（StoredName）與它的 data URL。一次只展開一張。</summary>
    private string? previewStoredName;
    private string? previewDataUrl;

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

    /// <summary>這個對象底下的所有對話，最近更新的排最前面。</summary>
    private IReadOnlyList<AiChatConversationInfo> conversations = [];

    /// <summary>目前正在看哪一段對話。空字串代表還沒有任何對話。</summary>
    private string currentConversationId = string.Empty;

    /// <summary>正在改名的是哪一段；空字串表示沒有在改名。</summary>
    private string renamingId = string.Empty;
    private string renamingText = string.Empty;
    private bool isSwitching;

    private AiChatConversationInfo? CurrentConversation
        => conversations.FirstOrDefault(x => string.Equals(x.Id, currentConversationId, StringComparison.Ordinal));

    /// <summary>目前這段還沒問過問題時，按鈕是停用的，要說清楚為什麼。</summary>
    private string NewConversationTooltip
        => CurrentConversation is { MessageCount: 0 } ? "目前已經是一段空白的新對話" : "開新對話";

    /// <summary>
    /// 任一個耗時或會改狀態的動作進行中。所有按鈕共用同一個閘門：
    /// 匯出 PDF 要起無頭瀏覽器（數秒、上百 MB），連點會把伺服器打爛。
    /// </summary>
    private bool IsBusy =>
        isAsking || isSavingEdit || isRegenerating || isDownloadingConversation || downloadingIndex >= 0
        || isReadingAttachments;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 視窗內容可能隨開關被重建，所以每次畫完都掛一次；JS 端以旗標擋掉重複掛載。
        if (!Visible || chatArea.Context is null)
        {
            return;
        }

        try
        {
            await JSRuntime.InvokeVoidAsync("meetingRecordChatAttach.attach", chatArea);
        }
        catch (JSException ex)
        {
            // 掛不上只是少了貼上與拖放，迴紋針仍然可用，不該讓整個視窗壞掉。
            Logger.LogWarning(ex, "Failed to attach AI chat paste/drop handlers.");
        }
        catch (JSDisconnectedException)
        {
            // 使用者已經離開頁面，沒有東西可掛。
        }
    }

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
        await LoadConversationsAsync(preferredId: null);
    }

    /// <summary>
    /// 重新列出對話清單，並決定要顯示哪一段。
    ///
    /// <para>
    /// 沒有指定就載**最近更新的那一段**；完全沒有對話時直接開一段新的——
    /// 不要讓使用者面對一個空畫面還得先按「開新對話」才能問問題。
    /// </para>
    /// </summary>
    private async Task LoadConversationsAsync(string? preferredId)
    {
        try
        {
            conversations = await AiChatService.ListConversationsAsync(Scope, TargetId);

            var target = preferredId is not null
                && conversations.Any(x => string.Equals(x.Id, preferredId, StringComparison.Ordinal))
                    ? preferredId
                    : conversations.FirstOrDefault()?.Id;

            if (target is null)
            {
                // 一段都沒有：開一段空的，使用者可以直接開始問。
                target = await AiChatService.CreateConversationAsync(Scope, TargetId);
                conversations = await AiChatService.ListConversationsAsync(Scope, TargetId);
            }

            currentConversationId = target;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading AI chat conversations failed. Scope={Scope}, TargetId={TargetId}", Scope, TargetId);
            errorMessage = $"載入對話清單失敗：{ex.Message}";
            return;
        }

        await LoadHistoryAsync();
    }

    /// <summary>切換到另一段對話。</summary>
    private async Task OnSelectConversationAsync(string conversationId)
    {
        if (IsBusy || isSwitching || string.Equals(conversationId, currentConversationId, StringComparison.Ordinal))
        {
            return;
        }

        isSwitching = true;
        try
        {
            currentConversationId = conversationId;
            await LoadHistoryAsync();
        }
        finally
        {
            isSwitching = false;
        }
    }

    /// <summary>
    /// 開一段新對話。
    /// <b>不呼叫模型、不會產生費用</b>——只是建一個空檔案，所以不跳費用確認。
    /// </summary>
    private async Task OnNewConversationAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // 目前這段一個字都還沒問，就是一段空白的新對話了。再開一段只會在共用的清單上
        // 多堆一列「新對話」，而且誰也分不出差別。
        if (CurrentConversation is { MessageCount: 0 })
        {
            return;
        }

        try
        {
            var created = await AiChatService.CreateConversationAsync(Scope, TargetId);
            await LoadConversationsAsync(created);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Creating AI chat conversation failed. Scope={Scope}, TargetId={TargetId}", Scope, TargetId);
            errorMessage = $"開新對話失敗：{ex.Message}";
        }
    }

    private void OnRenameStart(AiChatConversationInfo session)
    {
        renamingId = session.Id;
        renamingText = session.Title;
    }

    private void OnRenameCancel()
    {
        renamingId = string.Empty;
        renamingText = string.Empty;
    }

    private async Task OnRenameKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnRenameCommitAsync();
        }
        else if (FormKeyboardHelper.IsCancel(args))
        {
            OnRenameCancel();
        }
    }

    /// <summary>送出改名。空白視同取消——不要讓對話變成沒有名字。</summary>
    private async Task OnRenameCommitAsync()
    {
        if (string.IsNullOrWhiteSpace(renamingId) || string.IsNullOrWhiteSpace(renamingText))
        {
            OnRenameCancel();
            return;
        }

        var target = renamingId;
        var title = renamingText.Trim();
        OnRenameCancel();

        try
        {
            var outcome = await AiChatService.RenameConversationAsync(Scope, TargetId, target, title);
            if (outcome == UpdateOutcome.NotFound)
            {
                errorMessage = "這段對話已經被刪除了，請重新整理後再試。";
            }

            await LoadConversationsAsync(currentConversationId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Renaming AI chat conversation failed. ConversationId={ConversationId}", target);
            errorMessage = $"重新命名失敗：{ex.Message}";
        }
    }

    /// <summary>刪掉其中一段對話。會刪資料，所以跳二次確認。</summary>
    private async Task OnDeleteConversationAsync(AiChatConversationInfo session)
    {
        if (IsBusy)
        {
            return;
        }

        var confirmed = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除這段對話",
            Content = session.MessageCount > 0
                ? $"將刪除「{session.Title}」的全部 {session.MessageCount} 則訊息，所有人都會看不到，且無法復原。確定要刪除嗎？"
                : $"將刪除「{session.Title}」。確定要刪除嗎？",
            OkText = "刪除",
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
            await AiChatService.ClearHistoryAsync(Scope, TargetId, session.Id);

            // 刪掉的正好是目前這一段時，交給 LoadConversationsAsync 自己挑下一段
            // （沒有任何對話時它會開一段新的）。
            var preferred = string.Equals(session.Id, currentConversationId, StringComparison.Ordinal)
                ? null
                : currentConversationId;

            await LoadConversationsAsync(preferred);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Deleting AI chat conversation failed. ConversationId={ConversationId}", session.Id);
            errorMessage = $"刪除對話失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 只重新列出左側清單，不動目前顯示的訊息。
    ///
    /// <para>
    /// ⚠️ 每一個會改到對話內容的動作（提問、編輯、重新產生）結束後都要呼叫。
    /// 漏掉的話，第一次發問後左側仍然寫著「新對話」，而且因為則數還停在 0，
    /// 「開新對話」會一直是停用的。
    /// </para>
    /// </summary>
    private async Task RefreshConversationListAsync()
    {
        try
        {
            conversations = await AiChatService.ListConversationsAsync(Scope, TargetId);
        }
        catch (Exception ex)
        {
            // 清單沒刷新不該蓋掉剛剛得到的回答，記下來就好。
            Logger.LogWarning(ex, "Refreshing AI chat conversation list failed. Scope={Scope}, TargetId={TargetId}",
                Scope, TargetId);
        }
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

        // 預覽綁的是某一段對話裡的附件，換段或重載後就不該再掛著。
        ClosePreview();

        try
        {
            messages.AddRange(await AiChatService.GetHistoryAsync(Scope, TargetId, currentConversationId));
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
        var attachments = pendingAttachments.Select(x => x.Item).ToList();
        question = string.Empty;
        streamingAnswer = string.Empty;
        errorMessage = null;
        isAsking = true;

        // 先把提問放進畫面，使用者才不會覺得按了沒反應。實際落庫在服務層與回答一起做。
        // 附件在這裡只是顯示用的空殼（StoredName 空白＝還不能點），落庫後重載歷史就換成真的。
        messages.Add(new AiChatMessageItem(
            AiChatService.UserRole,
            asked,
            null,
            DateTime.Now,
            [.. attachments.Select(x => new AiChatAttachment(
                x.FileName,
                string.Empty,
                AiChatAttachmentPolicy.Classify(x.FileName) ?? AiChatAttachmentKind.Document,
                x.Content.LongLength))]));
        StateHasChanged();

        try
        {
            var answer = await AiChatService.AskAsync(
                Scope, TargetId, currentConversationId, asked, OnDelta, attachments: attachments);

            // 成功才清掉待送附件；失敗時留著，使用者改一下問題就能重送，不必重新挑檔案。
            pendingAttachments.Clear();

            // 用服務層回傳的內容重建整段歷史，順便把提問者名稱與落庫時間補正。
            await LoadHistoryAsync();
            await RefreshConversationListAsync();
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

    #region 附件（0.4.95）

    /// <summary>
    /// 迴紋針挑選、貼上、拖放三條路共用的入口（後兩者由 chat-attach.js 把檔案塞進同一個 InputFile）。
    /// 不合規則的檔案逐一跳提示後略過，其餘照收。
    /// </summary>
    private async Task OnAttachmentsSelectedAsync(InputFileChangeEventArgs args)
    {
        if (IsBusy)
        {
            return;
        }

        isReadingAttachments = true;
        try
        {
            foreach (var file in args.GetMultipleFiles(args.FileCount))
            {
                if (pendingAttachments.Count >= AiChatAttachmentPolicy.MaxAttachmentsPerQuestion)
                {
                    _ = MessageService.WarningAsync($"一次最多附上 {AiChatAttachmentPolicy.MaxAttachmentsPerQuestion} 個檔案，其餘已略過。");
                    break;
                }

                if (AiChatAttachmentPolicy.Validate(file.Name, file.Size) is { } error)
                {
                    _ = MessageService.WarningAsync(error);
                    continue;
                }

                var kind = AiChatAttachmentPolicy.Classify(file.Name);
                var limit = kind == AiChatAttachmentKind.Image
                    ? AiChatAttachmentPolicy.MaxImageBytes
                    : AiChatAttachmentPolicy.MaxDocumentBytes;

                await using var stream = file.OpenReadStream(limit);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                var content = buffer.ToArray();

                var isImage = kind == AiChatAttachmentKind.Image;
                var thumbnail = isImage && content.LongLength <= MaxThumbnailBytes
                    ? $"data:{AiChatAttachmentPolicy.GetImageMediaType(file.Name)};base64,{Convert.ToBase64String(content)}"
                    : null;

                pendingAttachments.Add(new PendingView(new PendingAttachment(file.Name, content), isImage, thumbnail));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to read AI chat attachments.");
            _ = MessageService.ErrorAsync("讀取附件失敗，請再試一次。");
        }
        finally
        {
            isReadingAttachments = false;

            // 讀完才換 key 重建 input（理由見 FileDropZone）：否則下次選同一個檔案不會觸發 change。
            attachInputKey++;
            StateHasChanged();
        }
    }

    private void OnRemovePending(int index)
    {
        if (index >= 0 && index < pendingAttachments.Count)
        {
            pendingAttachments.RemoveAt(index);
        }
    }

    /// <summary>圖片：在訊息下方展開／收起預覽。文件：直接下載。</summary>
    private async Task OnOpenAttachmentAsync(AiChatAttachment attachment)
    {
        if (IsBusy || string.IsNullOrEmpty(attachment.StoredName))
        {
            return;
        }

        if (attachment.Kind == AiChatAttachmentKind.Image && previewStoredName == attachment.StoredName)
        {
            ClosePreview();
            return;
        }

        var content = await AiChatService.ReadAttachmentAsync(Scope, TargetId, currentConversationId, attachment);
        if (content is null)
        {
            _ = MessageService.WarningAsync($"找不到附件「{attachment.FileName}」，可能已被刪除。");
            return;
        }

        if (attachment.Kind == AiChatAttachmentKind.Image)
        {
            previewStoredName = attachment.StoredName;
            previewDataUrl = $"data:{AiChatAttachmentPolicy.GetImageMediaType(attachment.FileName)};base64,{Convert.ToBase64String(content)}";
            return;
        }

        await FileDownloadInterop.SaveBytesAsync(attachment.FileName, content, "application/octet-stream");
    }

    private void ClosePreview()
    {
        previewStoredName = null;
        previewDataUrl = null;
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    #endregion

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
            var outcome = await AiChatService.UpdateMessageAsync(Scope, TargetId, currentConversationId, index, original, newContent);
            if (!HandleUpdateOutcome(outcome))
            {
                return;
            }

            await LoadHistoryAsync();
            await RefreshConversationListAsync();
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
                Scope, TargetId, currentConversationId, index, original.Content, newQuestion, OnDelta);

            await LoadHistoryAsync();
            await RefreshConversationListAsync();
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
            var fileName = AiChatDocumentExporter.BuildMessageFileName(TargetName, index + 1, exportedAt, CurrentConversation?.Title);

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
            var html = AiChatDocumentExporter.BuildConversationHtml(TargetName, messages, exportedAt, CurrentConversation?.Title);
            var fileName = AiChatDocumentExporter.BuildConversationFileName(TargetName, exportedAt, CurrentConversation?.Title);

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

        // 單則與整段對話都走這裡，所以提示只寫一次；檔名本身就分得出是哪一種。
        //
        // ⚠️ 走 NotificationService（右下角）而不是本檔其他地方用的 MessageService（頂部置中）：
        //    全站的「下載完成」一律在右下角，與「已複製到剪貼簿」那種瞬時操作回饋不同層級。
        // ⚠️ NotificationType.Warning 是刻意與全站現況對齊、不是筆誤——
        //    全專案 30 處成功提示都用 Warning，這裡單獨改成 Success 只會多一種不一致。
        _ = NotificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = $"已下載「{fileName}」。",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });
    }

    #endregion

    /// <summary>
    /// 刪掉目前這一段對話。與清單上那顆刪除鈕是同一件事，所以直接走同一條路徑——
    /// 各寫一份的話，這裡刪完之後不重載清單，currentConversationId 會指向一個
    /// 已經不存在的檔案。
    /// </summary>
    private async Task OnClearAsync()
    {
        if (CurrentConversation is { } current)
        {
            await OnDeleteConversationAsync(current);
        }
    }

    private async Task OnCancelAsync()
    {
        // 讓下次開啟時重新載入（可能是另一個對象，或期間有人問了新問題）。
        loadedTarget = null;
        CancelEditState();

        // 待送附件不留到下次開啟：下次可能是另一個專案，帶著上一個專案的檔案去問是錯的。
        pendingAttachments.Clear();
        ClosePreview();
        await VisibleChanged.InvokeAsync(false);
    }
}
