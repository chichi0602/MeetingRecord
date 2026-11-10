using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntDesign;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 表單的「改過了沒」判斷，以及「改過但沒存」的確認框。
///
/// <para>
/// 抽成共用純函式的理由與 <see cref="FormKeyboardHelper"/> 相同：有 10 個呼叫端、同一段文案，
/// 各寫一份必然走樣。而且這裡的邏輯是 string 進、bool 出，可以用 xUnit 測——
/// 本專案沒有 bUnit，寫進 <c>.razor.cs</c> 的判斷就只能靠眼睛驗。
/// </para>
///
/// <para>
/// ⚠️ <b>不要改用 <c>EditContext.IsModified()</c>。</b>本專案的 <see cref="InputWatcher"/>
/// 撈到的是**外層 <c>EditForm</c>** 的 EditContext，而真正的欄位住在**內層 <c>AntDesign.Form</c>**
/// 底下，欄位變更的通知打在內層，外層永遠回 <c>false</c>。
/// （<c>Validate()</c> 之所以能用，是因為它驗的是同一個 model 實例，不依賴 field-change 通知。）
/// </para>
/// </summary>
public static class FormDirtyHelper
{
    /// <summary>
    /// 分隔 model 快照與各個 extras。
    /// 用 Unit Separator 是因為使用者打不出這個字元，所以不會與內容本身混淆。
    /// </summary>
    private const char ExtraSeparator = '';

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        // 目前的 AdapterModel 沒有循環參照（最深 4 層，單向樹），但預設行為是丟 JsonException——
        // 那會在使用者按下「取消」的當下炸開。日後有人加了回頭參照時，
        // 這裡寧可靜靜地少比一個欄位（結果是多問一次確認），也不要整個取消流程壞掉。
        ReferenceHandler = ReferenceHandler.IgnoreCycles,

        // 快照只拿來做字串比對，永遠不會進 HTML。關掉逸出讓中文在 log 裡看得懂，
        // 順便讓字串短掉數倍。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 拍一份表單當下的快照，開啟表單時呼叫一次。
    ///
    /// <para>
    /// ⚠️ <paramref name="extras"/> 是給「不在 model 上的暫存狀態」用的（待上傳檔案之類）。
    /// 簽章刻意是 <c>string?</c> 而不是 <c>object?</c>：<c>IBrowserFile</c> 身上帶著 Stream，
    /// 直接丟進序列化器得到的東西與使用者選了哪個檔案無關，**所有檔案都會長得一樣**，
    /// 於是「換了一個檔案」永遠判不出來。用 string 逼呼叫端自己講清楚要比什麼
    /// （檔名＋大小就夠了，見 <c>ProjectViewView.DescribePendingFiles()</c>）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 同一個呼叫點的 <paramref name="extras"/> 個數必須固定。拍快照時傳 1 個、
    /// 比對時傳 0 個，會因為分隔符數量不同而永遠判定成 dirty。
    /// </para>
    /// </summary>
    public static string Capture(object? model, params string?[] extras)
    {
        // 字面 null 在 C# 會綁成「整個陣列是 null」而不是「一個是 null 的元素」。
        // 對呼叫端丟例外太苛刻——那會在使用者按取消的當下炸開，而語意上它就等於「沒有 extras」。
        // 真的要傳一個 null 元素時寫 (string?)null 即可。
        extras ??= [];

        // 用 model.GetType() 而不是泛型參數：呼叫端常常拿著基底型別或 private nested 型別
        // （例如 TodoExtractionModal 的 Candidate），用宣告型別序列化會漏掉子類別的屬性。
        var builder = new StringBuilder(
            model is null
                ? "null"
                : JsonSerializer.Serialize(model, model.GetType(), SnapshotOptions));

        foreach (var extra in extras)
        {
            builder.Append(ExtraSeparator).Append(extra ?? string.Empty);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 表單改過了沒。
    ///
    /// <para>
    /// ⚠️ <paramref name="snapshot"/> 是 <c>null</c> 時<b>一律回 true</b>。那代表開啟表單時
    /// 忘了呼叫 <see cref="Capture"/>，此時寧可多問一次（使用者只是多按一下），
    /// 也不要靜靜地把沒存的輸入丟掉。漏拍快照的頁面症狀是「每次取消都跳確認」，
    /// 一試就看得出來；反過來的失敗模式是靜默吃掉使用者打好的字。
    /// </para>
    /// </summary>
    public static bool IsDirty(string? snapshot, object? model, params string?[] extras)
        => snapshot is null
            || !string.Equals(snapshot, Capture(model, extras), StringComparison.Ordinal);

    /// <summary>
    /// 「改過但沒存」的確認框選項。
    ///
    /// <para>
    /// 文案沿用 0.4.82 <c>MarkdownEditorModal</c> 已經出貨的那一份，一字不改——
    /// 那份是使用者已經看過的，同一件事沒有理由有兩種說法。
    /// （<c>subject = "這份會議紀錄"</c> 會產出與它完全相同的字串，有測試釘住。）
    /// </para>
    /// </summary>
    /// <param name="subject">主詞，例如「這筆分類」。會接在句首。</param>
    /// <param name="extraWarning">額外的後果說明，例如重開會再計費。接在中間。</param>
    public static ConfirmOptions BuildDiscardOptions(string subject, string? extraWarning = null)
        => new()
        {
            Title = "放棄編修",
            Content = $"{subject}已經改過但還沒儲存，關閉之後改動會消失。"
                + (string.IsNullOrWhiteSpace(extraWarning) ? string.Empty : extraWarning)
                + "確定要放棄嗎？",
            OkText = "放棄",
            CancelText = "繼續編修",
            MaskClosable = false,

            // 套 Danger：這個方向會讓使用者失去已經打好的字，而且救不回來。
            // 與速查表 §6.3「Danger 跟『覆蓋』走，不跟『花錢』走」一致——
            // 這裡被覆蓋掉的是使用者的輸入。
            OkButtonProps = new ButtonProps { Danger = true },
        };

    /// <summary>跳出「改過但沒存」的確認框。回傳 true 代表使用者選擇放棄。</summary>
    public static Task<bool> ConfirmDiscardAsync(
        ModalService modalService,
        string subject,
        string? extraWarning = null)
    {
        ArgumentNullException.ThrowIfNull(modalService);

        return modalService.ConfirmAsync(BuildDiscardOptions(subject, extraWarning));
    }
}
