using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 可拖拉的檔案選取區。
///
/// <para>
/// 抽成共用元件的理由：全系統有兩個上傳入口（專案附件、會議影音檔），
/// 先前各寫各的 <c>&lt;InputFile&gt;</c>，連提示文字的排版都不一樣。
/// </para>
///
/// <para>
/// ⚠️ 它<b>不接管上傳</b>，只是把「選檔案」這件事包起來：<see cref="OnChange"/> 收到的
/// 仍然是原本的 <see cref="InputFileChangeEventArgs"/>，呼叫端的驗證、暫存清單與服務層
/// 一行都不用改。
/// </para>
/// </summary>
public partial class FileDropZone : ComponentBase
{
    private bool isDragging;

    /// <summary>
    /// 進入與離開的次數差。
    ///
    /// <para>
    /// ⚠️ 不能只用一個 bool：滑鼠移到子元素上時瀏覽器會先對子元素發 dragenter、
    /// 再對原本的元素發 dragleave，只看 dragleave 會讓外框在拖曳過程中不停閃爍。
    /// 用計數器配對，歸零才算真的離開。
    /// </para>
    /// </summary>
    private int dragDepth;

    /// <summary>
    /// 底下那個 file input 的 <c>@key</c>。每次選完檔案就換一個，強制重建 input。
    /// 理由見 .razor 的註解：input 不會自己清空 value，重選同一個檔案不會觸發 change。
    /// </summary>
    private Guid resetToken = Guid.NewGuid();

    /// <summary>區塊中央的主要文字。</summary>
    [Parameter]
    public string Title { get; set; } = "把檔案拖到這裡，或點擊選擇檔案";

    /// <summary>標題下方的補充說明（大小上限、支援格式等）。</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>是否可多選。</summary>
    [Parameter]
    public bool Multiple { get; set; }

    /// <summary>
    /// 檔案挑選對話框的格式過濾。
    /// ⚠️ <b>對拖進來的檔案無效</b>——瀏覽器只在挑選對話框套用它，
    /// 需要限制格式的呼叫端必須在 <see cref="OnChange"/> 裡自己再擋一次。
    /// </summary>
    [Parameter]
    public string? Accept { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>選到檔案（點選或拖入皆同）。</summary>
    [Parameter]
    public EventCallback<InputFileChangeEventArgs> OnChange { get; set; }

    private void OnDragEnter()
    {
        if (Disabled)
        {
            return;
        }

        dragDepth++;
        isDragging = true;
    }

    private void OnDragLeave()
    {
        if (Disabled)
        {
            return;
        }

        dragDepth = Math.Max(0, dragDepth - 1);
        isDragging = dragDepth > 0;
    }

    /// <summary>
    /// 只重設視覺狀態。
    ///
    /// ⚠️ <b>刻意不做任何事、也不 preventDefault</b>：檔案是由瀏覽器原生交給底下那個
    /// file input 的，攔截或取消預設行為都會讓檔案進不來。
    /// </summary>
    private void OnDrop()
    {
        dragDepth = 0;
        isDragging = false;
    }

    private async Task OnFileChangedAsync(InputFileChangeEventArgs args)
    {
        dragDepth = 0;
        isDragging = false;

        if (OnChange.HasDelegate)
        {
            await OnChange.InvokeAsync(args);
        }

        // ⚠️ 這裡**絕對不可以**換 key。
        //
        // 0.4.77 曾經在這一行換 key，結果是：Blazor 立刻銷毀並重建 <input type="file">，
        // 而呼叫端剛拿到的 IBrowserFile 是綁在**那個已被銷毀的元素**上的。
        // 稍後 OpenReadStream 去 JS 端查它就會拿到 null，錯誤訊息是
        // 「Cannot read properties of null (reading '_blazorFilesById')」——
        // 會議影音檔與專案附件都是「先存起來、稍後才讀」，兩條路徑全部上傳失敗。
        //
        // 重建 input 的時機改成由呼叫端在「用完這個檔案之後」呼叫 Reset()。
    }

    /// <summary>
    /// 重建底下的 file input，讓「同一個檔案」可以再選一次。
    ///
    /// <para>
    /// input 在 change 之後不會清空 value，重新拖同一個檔案時瀏覽器認為沒有變化、
    /// 不再觸發 change，畫面就完全沒反應。（點擊挑選的路徑 Blazor 自己會在 click 時清 value，
    /// 但**拖放不會觸發 click**，所以拖放這條路徑需要這個方法。）
    /// </para>
    ///
    /// <para>
    /// ⚠️ 呼叫端必須在**確定不再需要那個 <see cref="IBrowserFile"/> 之後**才呼叫——
    /// 移除待上傳檔案時、或上傳完成之後。太早呼叫會讓檔案讀不出來（理由見
    /// <see cref="OnFileChangedAsync"/> 的註解）。
    /// </para>
    /// </summary>
    public void Reset()
    {
        resetToken = Guid.NewGuid();
        StateHasChanged();
    }
}
