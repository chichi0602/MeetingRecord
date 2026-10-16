using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 使用說明頁（1.0.42）的前端橋接：/ 快速搜尋、關鍵字高亮、跨分類錨點。
///
/// <para>
/// ⚠️ 失敗一律吞掉並記 log，不往上拋（理由同 <see cref="TodoAutoFitInterop"/>）：
/// 高亮或快捷鍵掛不上只是少了便利，元件事件裡的未處理例外卻會把整個 circuit 斷掉。
/// 離開頁面時 <c>JSDisconnectedException</c>（繼承自 <see cref="JSException"/>）是常態。
/// </para>
///
/// 對應的前端實作在 <c>wwwroot/js/help-view.js</c>。
/// </summary>
public sealed class HelpViewInterop
{
    private const string AttachFunction = "meetingRecordHelpView.attach";
    private const string DetachFunction = "meetingRecordHelpView.detach";
    private const string HighlightFunction = "meetingRecordHelpView.highlight";
    private const string ScrollToFunction = "meetingRecordHelpView.scrollTo";

    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<HelpViewInterop> logger;

    public HelpViewInterop(IJSRuntime jsRuntime, ILogger<HelpViewInterop> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    public Task AttachAsync<TComponent>(ElementReference root, DotNetObjectReference<TComponent> reference)
        where TComponent : class
        => InvokeAsync(AttachFunction, root, reference);

    public Task DetachAsync(ElementReference root) => InvokeAsync(DetachFunction, root);

    public Task HighlightAsync(ElementReference root, IReadOnlyList<string> terms) => InvokeAsync(HighlightFunction, root, terms);

    public Task ScrollToAsync(ElementReference root, string? id) => InvokeAsync(ScrollToFunction, root, id);

    private async Task InvokeAsync(string identifier, params object?[] args)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Help view interop failed. Function={Function}", identifier);
        }
        catch (InvalidOperationException)
        {
            // 預先算繪階段沒有 JS 可呼叫。
        }
    }
}
