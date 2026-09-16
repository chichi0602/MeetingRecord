using Microsoft.AspNetCore.Components.Web;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 表單的鍵盤判斷。抽成共用純函式，避免每個畫面各寫一份而規則走樣
/// （0.4.77 之前有 7 份各自為政的 handler，沒有一份做輸入法防護）。
/// </summary>
public static class FormKeyboardHelper
{
    /// <summary>
    /// 這個按鍵是不是「送出」。
    ///
    /// <para>
    /// ⚠️ <b><see cref="KeyboardEventArgs.IsComposing"/> 這個判斷是整段的重點</b>：
    /// 中文輸入法用 Enter 確認候選字，沒有這道防護的話，使用者在標題欄打「會議」按 Enter 選字，
    /// 整個對話框就存檔關閉了。<see cref="KeyboardEventArgs"/> 沒有 <c>KeyCode</c>，
    /// 所以「檢查 229」在 Blazor 端做不到，<c>IsComposing</c> 是唯一的途徑。
    /// </para>
    ///
    /// <para>
    /// 帶修飾鍵的 Enter 一律不算送出。其中 <b>Shift+Enter 是多行欄位的換行鍵</b>——
    /// 這裡不處理它，只要不回報成送出，瀏覽器的原生行為就會換行（AI 問答一直是這樣用的）。
    /// </para>
    /// </summary>
    public static bool IsSubmit(KeyboardEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Key == "Enter"
            && !args.IsComposing
            && !args.ShiftKey
            && !args.CtrlKey
            && !args.AltKey
            && !args.MetaKey;
    }

    /// <summary>
    /// 這個按鍵是不是「取消」。Esc 在不同瀏覽器回報的字串不一致，兩種都要收。
    /// </summary>
    public static bool IsCancel(KeyboardEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Key is "Escape" or "Esc";
    }
}
