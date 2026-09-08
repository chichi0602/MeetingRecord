using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.AiChat;

namespace MeetingRecord.Web.Components.Views.Projects;

/// <summary>
/// AI 問答視窗。專案層級與單一會議共用同一個元件，差別只在 <see cref="Scope"/> 與 <see cref="TargetId"/>。
/// </summary>
public partial class AiChatModal : ComponentBase
{
    [Inject]
    private AiChatService AiChatService { get; set; } = default!;

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

    private readonly List<AiChatMessageItem> messages = [];

    private string question = string.Empty;
    private string streamingAnswer = string.Empty;
    private string? sourceNotice;
    private string? errorMessage;
    private bool isAsking;

    /// <summary>上一次載入歷史用的對象，用來判斷是否需要重新載入。</summary>
    private (AiChatScope Scope, int TargetId)? loadedTarget;

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

    /// <summary>Enter 送出、Shift+Enter 換行。</summary>
    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key != "Enter" || args.ShiftKey)
        {
            return;
        }

        await OnAskAsync();
    }

    private async Task OnAskAsync()
    {
        if (isAsking || string.IsNullOrWhiteSpace(question))
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

    private async Task OnClearAsync()
    {
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
        await VisibleChanged.InvokeAsync(false);
    }
}
