namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 表單送出閘門（1.0.62）：同一個表單視窗的 Enter 與「確定」共用一個，同一時間只會送出一次。
///
/// <para>
/// 為什麼要有它：按 Enter 送出時要先等一下（<see cref="EnterSettleDelay"/>）——AntDesign Input 預設 change／blur
/// 才回寫繫結值，焦點還在欄位裡就存會拿到舊值。1.0.61 以前每個表單都是「先 <c>Task.Delay(200)</c>、再進存檔防護」，
/// 等候的那 200ms 不受防護：這段時間按「確定」或長按 Enter，第一次存完、防護解除後，延遲的那一次又存一次，
/// 新增出兩筆（M0 基準端對端測試發現）。這裡在等候之前就把 <see cref="IsBusy"/> 立起來。
/// </para>
///
/// <para>
/// 用法：<c>OnOk</c> 呼叫 <c>submitGate.RunAsync(SaveModalAsync)</c>；Enter 呼叫
/// <c>submitGate.RunAsync(SaveModalAsync, FormSubmitGate.EnterSettleDelay)</c>；視窗的 <c>ConfirmLoading</c> 讀 <see cref="IsBusy"/>。
/// <c>FormEnterGuardTests</c> 會檢查八個 CRUD 表單都這樣寫。
/// </para>
/// </summary>
public sealed class FormSubmitGate
{
    /// <summary>按 Enter 送出前等候繫結回寫的時間。</summary>
    public static readonly TimeSpan EnterSettleDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>送出中（含等候回寫）。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>沒有在送出中才執行 <paramref name="submit"/>；送出中再呼叫一律忽略。</summary>
    public async Task RunAsync(Func<Task> submit, TimeSpan delay = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            await submit();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
