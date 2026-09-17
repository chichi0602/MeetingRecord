namespace MeetingRecord.Share.Enums;

/// <summary>
/// 會產生外部 API 費用的功能別。以 int 儲存於資料庫，顯示文字集中在
/// <see cref="AiUsageFeatureText.Describe"/>。
///
/// <para>
/// ⚠️ 數值一旦上線就**永不重編**——帳本是永久保留的歷史紀錄，改動數值等於改寫過去。
/// </para>
/// </summary>
public enum AiUsageFeature
{
    /// <summary>會議紀錄生成的最終整理（reduce）。一趟工作固定一次。</summary>
    MeetingDraft = 0,

    /// <summary>
    /// 逐字稿分段摘要（map）。一趟工作會有 <b>N 次</b>，N 是逐字稿切出來的段數。
    ///
    /// <para>
    /// 刻意與 <see cref="MeetingDraft"/> 分開：map 是 N 次短輸出、reduce 是 1 次長輸出，
    /// 成本結構完全不同。合併之後「這個月會議紀錄為什麼特別貴」就永遠答不出來
    /// （答案幾乎都是「有幾場長會議走了分段摘要」）。要合著看很容易，拆開來看在事後不可能。
    /// </para>
    /// </summary>
    MeetingDraftChunkSummary = 1,

    /// <summary>語音轉錄。一個音檔會有 <b>N 次</b>，N 是以 15 分鐘切出來的段數。</summary>
    Transcription = 2,

    /// <summary>AI 問答。提問與「重新產生答案」各算一次。</summary>
    AiChat = 3,

    /// <summary>從會議紀錄抽出待辦事項。一次固定一次。</summary>
    TodoExtraction = 4,
}

/// <summary>功能別的顯示文字與計費單位判斷（UI、日誌與計價共用）。</summary>
public static class AiUsageFeatureText
{
    public static string Describe(AiUsageFeature feature) => feature switch
    {
        AiUsageFeature.MeetingDraft => "會議紀錄生成",
        AiUsageFeature.MeetingDraftChunkSummary => "逐字稿分段摘要",
        AiUsageFeature.Transcription => "語音轉錄",
        AiUsageFeature.AiChat => "AI 問答",
        AiUsageFeature.TodoExtraction => "待辦事項擷取",
        _ => "未知",
    };

    /// <summary>
    /// 這個功能是以 token 計費（true）還是以音訊時長計費（false）。
    ///
    /// <para>
    /// **計費單位由功能唯一決定**，所以帳本刻意不另存一個單位欄位——
    /// 兩個並存必然會出現互相矛盾的資料。
    /// </para>
    /// </summary>
    public static bool IsTokenBased(AiUsageFeature feature) => feature != AiUsageFeature.Transcription;
}
