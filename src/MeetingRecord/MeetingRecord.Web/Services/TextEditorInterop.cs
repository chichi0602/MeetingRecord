using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>編輯區在某個瞬間的游標與捲動位置。</summary>
/// <param name="Caret">游標位置（UTF-16 code unit）。</param>
/// <param name="ScrollTop">捲動位置（px）。</param>
public readonly record struct TextEditorState(int Caret, double ScrollTop);

/// <summary>
/// <c>&lt;textarea&gt;</c> 的選取與捲動。搜尋到的位置要反白給使用者看，
/// 而那件事只有 DOM 做得到。
///
/// <para>
/// 找字與取代**不在這裡**，在 <c>MeetingRecord.Business.Helpers.TextSearchHelper</c>：
/// 那些是純字串運算，留在 C# 才測得到。這支只負責把算好的索引變成畫面上的反白。
/// </para>
///
/// <para>
/// ⚠️ 失敗一律吞掉並記 log，不往上拋。反白不成功只是「使用者要自己找一下」，
/// 但在 Blazor Server 上，元件事件裡的未處理例外會把整個 circuit 斷掉——
/// 為了一個視覺輔助賠掉使用者正在編修的內容，這個交換太糟了。
/// </para>
///
/// 對應的前端實作在 <c>wwwroot/js/text-editor.js</c>。
/// </summary>
public sealed class TextEditorInterop
{
    private const string SelectRangeFunction = "meetingRecordTextEditor.selectRange";
    private const string GetStateFunction = "meetingRecordTextEditor.getState";
    private const string SetScrollTopFunction = "meetingRecordTextEditor.setScrollTop";

    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<TextEditorInterop> logger;

    public TextEditorInterop(IJSRuntime jsRuntime, ILogger<TextEditorInterop> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    /// <summary>選取並捲到指定範圍。</summary>
    public async Task SelectRangeAsync(ElementReference element, int start, int length)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(SelectRangeFunction, element, start, length);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Text editor selectRange failed.");
        }
        catch (InvalidOperationException ex)
        {
            // 元件已經被處置，或是預先算繪階段還沒有 JS 可用。
            logger.LogDebug(ex, "Text editor selectRange skipped.");
        }
    }

    /// <summary>讀取當下的游標與捲動位置。取不到時回全 0，呼叫端據此把畫面留在原地。</summary>
    public async Task<TextEditorState> GetStateAsync(ElementReference element)
    {
        try
        {
            return await jsRuntime.InvokeAsync<TextEditorState>(GetStateFunction, element);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Text editor getState failed.");
            return default;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Text editor getState skipped.");
            return default;
        }
    }

    /// <summary>還原捲動位置。</summary>
    public async Task SetScrollTopAsync(ElementReference element, double scrollTop)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(SetScrollTopFunction, element, scrollTop);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Text editor setScrollTop failed.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Text editor setScrollTop skipped.");
        }
    }
}
