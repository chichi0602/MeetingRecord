using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Services;

/// <summary>
/// 待辦頁「列數跟著畫面高度走」的量測橋接（0.4.86）。
///
/// <para>
/// 只負責掛上／收掉觀察者。量到的數字由 JS 主動回呼元件的
/// <c>OnAutoFitMeasuredAsync</c>——視窗大小改變沒有對應的 C# 事件，只能用推的。
/// 換算成列數的算術在 <c>TodoAutoFit</c>，不在 JS 裡（本專案沒有 bUnit）。
/// </para>
///
/// <para>
/// ⚠️ 失敗一律吞掉並記 log，不往上拋。量不到高度只是「列數退回預設值」，
/// 但元件事件裡的未處理例外會被版面的 <c>ErrorBoundary</c> 接住、整塊頁面內容換成錯誤畫面——
/// 為了一個版面微調賠掉使用者正在做的事，這個交換太糟了。
/// </para>
///
/// 對應的前端實作在 <c>wwwroot/js/todo-auto-fit.js</c>。
/// </summary>
public sealed class TodoAutoFitInterop
{
    private const string ObserveFunction = "meetingRecordTodoAutoFit.observe";
    private const string RemeasureFunction = "meetingRecordTodoAutoFit.remeasure";
    private const string DisconnectFunction = "meetingRecordTodoAutoFit.disconnect";

    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<TodoAutoFitInterop> logger;

    public TodoAutoFitInterop(IJSRuntime jsRuntime, ILogger<TodoAutoFitInterop> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
    }

    /// <summary>開始觀察，並立刻回報一次目前的量測值。</summary>
    public async Task ObserveAsync<TComponent>(ElementReference root, DotNetObjectReference<TComponent> reference)
        where TComponent : class
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(ObserveFunction, root, reference);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Todo auto-fit observe failed.");
        }
        catch (InvalidOperationException ex)
        {
            // 元素還沒綁上（區塊沒有渲染）或元件已經被處置：略過即可。
            logger.LogDebug(ex, "Todo auto-fit observe skipped.");
        }
    }

    /// <summary>
    /// 改完每頁筆數、畫面重繪之後叫一次，讓控制器看到新的剩餘空間再決定要不要繼續補。
    /// ⚠️ 少了它就只會調整一次：JS 端的 ResizeObserver 只看寬度，而改列數只會改高度。
    /// </summary>
    public async Task RemeasureAsync(ElementReference root)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(RemeasureFunction, root);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Todo auto-fit remeasure skipped.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Todo auto-fit remeasure skipped.");
        }
    }

    /// <summary>停止觀察並清掉掛在根元素上的 CSS 變數。</summary>
    public async Task DisconnectAsync(ElementReference root)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync(DisconnectFunction, root);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Todo auto-fit disconnect skipped.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Todo auto-fit disconnect skipped.");
        }
    }
}
