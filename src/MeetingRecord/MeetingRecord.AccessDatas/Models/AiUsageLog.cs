using MeetingRecord.Share.Enums;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 一次會產生外部 API 費用的 AI 呼叫。**一次呼叫一列**，永久保留。
///
/// <para>
/// ⚠️ 「一次使用者動作 = 一次呼叫」是錯的：一趟會議紀錄生成會寫下 N 筆分段摘要＋1 筆最終整理，
/// 一個音檔會寫下 N 筆轉錄。帳本記的是**實際呼叫**，不是使用者按了幾次按鈕。
/// </para>
///
/// <para>
/// 兩種計費單位共用這一張表：文字生成填 <see cref="InputTokens"/>／<see cref="OutputTokens"/>，
/// 語音轉錄填 <see cref="AudioSeconds"/>，另一組一律為 null。刻意**不另設單位欄位**——
/// 單位由 <see cref="Feature"/> 唯一決定（見 <see cref="AiUsageFeatureText.IsTokenBased"/>），
/// 兩個並存必然會出現互相矛盾的資料。分成兩張表則會讓每一個查詢都變成 UNION。
/// </para>
///
/// <para>
/// 刻意**沒有任何外鍵**：帳本是既成事實的紀錄，不該因為會議或使用者被刪除而被連帶刪掉，
/// 也不該讓刪除操作被 Restrict 擋住。<see cref="UserName"/> 是當下的名字快照，
/// 與 <c>Meeting.DraftPromptTemplateName</c> 同一個設計。
/// </para>
///
/// <para>
/// 這張表沒有回填：0.4.80 之前的呼叫沒有留下任何用量資料，事後補不回來。
/// 畫面上要明示統計起始日。
/// </para>
/// </summary>
public class AiUsageLog
{
    public int Id { get; set; }

    /// <summary>
    /// 呼叫結束的時間（成功或失敗皆是）。
    /// ⚠️ 一律 <c>DateTime.Now</c>（本地時間），與全站一致——每日分桶是照本地日曆切的，
    /// 這裡改用 UTC 會讓趨勢圖與系統其他地方差 8 小時。
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    public AiUsageFeature Feature { get; set; }

    public AiUsageOutcome Outcome { get; set; }

    /// <summary>供應商名稱快照，例如 AzureOpenAI。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>deployment／model 名稱快照。**單價是以這個字串查表的**。</summary>
    public string Model { get; set; } = string.Empty;

    #region token 型（語音轉錄一律 null）

    /// <summary>
    /// 輸入 token。
    /// ⚠️ null 代表「供應商沒回報用量」，**不是 0**——失敗與取消一定是 null，
    /// 而 0 會讓人以為那次呼叫不用錢。
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>輸出 token。單價通常是輸入的 3～4 倍，所以一定要與輸入分開存。</summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// 命中提示詞快取的輸入 token。
    /// ⚠️ 已經**含在** <see cref="InputTokens"/> 裡，不可再加一次。
    /// 這一版不做快取的差別計價，留著是為了解釋「為什麼實際帳單比估算低」。
    /// </summary>
    public int? CachedInputTokens { get; set; }

    #endregion

    #region 時長型（文字生成一律 null）

    /// <summary>這一段實際送出的音訊秒數。</summary>
    public double? AudioSeconds { get; set; }

    /// <summary>
    /// 時長是推估來的（FFmpeg 回報 <c>Duration: N/A</c>，改以 mp3 位元組回推）。
    /// 標記起來，才不會讓推估值看起來跟實測值一樣可信。
    /// </summary>
    public bool IsAudioDurationEstimated { get; set; }

    #endregion

    #region 估算金額（單價未設定時一律 null，不可填 0）

    /// <summary>
    /// 寫入當下算好的估算金額。
    ///
    /// <para>
    /// ⚠️ **SQLite 把 decimal 存成 TEXT**：<c>ORDER BY</c> 是字典序（<c>"10.0" &lt; "9.0"</c>）、
    /// <c>WHERE &gt; 1</c> 同理。這個欄位**只能撈進記憶體再加總**，絕不可在 SQL 端排序或比較大小。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 單價沒設定時是 null 而不是 0——0 會讓總額靜靜地少報，而且看起來完全正常。
    /// </para>
    /// </summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>估算金額的幣別代碼，例如 USD。這是**定價**幣別，換算後的顯示幣別見 <see cref="ConvertedCurrency"/>。</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// 當下用的單價快照。
    ///
    /// <para>
    /// 金額**寫入時算好存起來**而不是讀取時算，因為單價會變，而這頁最主要的用途是
    /// 「本月 vs 上月」——若歷史被新單價重算，那個比較就失去意義。
    /// 把單價一起存下來，日後真要修正某段期間的定價，也看得出哪些列受影響。
    /// </para>
    /// </summary>
    public decimal? InputPricePerMillion { get; set; }

    public decimal? OutputPricePerMillion { get; set; }

    public decimal? AudioPricePerMinute { get; set; }

    /// <summary>
    /// 呼叫當下的匯率（0.4.88）：1 單位 <see cref="Currency"/> 可換多少 <see cref="ConvertedCurrency"/>。
    ///
    /// <para>
    /// 與單價一樣是**寫入當下的快照**。匯率天天在動，若讀取時才用當天匯率換算，
    /// 今天匯率一變，去年的帳就整批跟著變——「本月 vs 上月」會失去意義。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 與 <see cref="EstimatedCost"/> 同樣是 SQLite 的 TEXT 欄位：
    /// **不可在 SQL 端 <c>ORDER BY</c>、比大小或 <c>SUM</c>**，只能 <c>IS NOT NULL</c> 篩選後撈進記憶體算。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 抓不到匯率時是 null 而不是 1——1 會讓台幣金額少報三十幾倍，而畫面上完全看不出來。
    /// </para>
    /// </summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// 換算目標幣別代碼，例如 TWD（0.4.88）。
    /// 少了這一欄，<see cref="ExchangeRate"/> 的 31.86 無法自證是換成台幣還是別的幣別。
    /// </summary>
    public string? ConvertedCurrency { get; set; }

    #endregion

    #region 歸屬

    /// <summary>觸發者的使用者 Id；背景工作由佇列帶進來，取不到時為 null。</summary>
    public int? UserId { get; set; }

    /// <summary>觸發者姓名快照。使用者改名或被刪除後，帳本仍看得出當初是誰。</summary>
    public string? UserName { get; set; }

    public int? MeetingId { get; set; }

    public int? ProjectId { get; set; }

    #endregion

    /// <summary>失敗原因（截斷）。成功時為 null。</summary>
    public string? ErrorMessage { get; set; }
}
