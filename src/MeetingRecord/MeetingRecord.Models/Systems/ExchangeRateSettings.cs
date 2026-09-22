using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 匯率換算設定（0.4.88）。把「AI 用量分析」的估算金額從定價幣別（<c>LlmSettings.Currency</c>，
/// 目前是美金）換算成顯示幣別（<see cref="TargetCurrency"/>，目前是台幣）。
///
/// <para>
/// ⚠️ 刻意獨立成一個頂層區段，不塞進 <see cref="LlmSettings"/>：那裡是「供應商連線與 deployment 定價」，
/// 已經有 <see cref="IValidatableObject"/> 且被三支 provider 讀。把一個 HTTP 端點塞進去，
/// 會讓「換 LLM 廠商」與「換匯率來源」兩件無關的事互相牽動。全站慣例也是一個關注點一個區段。
/// </para>
///
/// <para>
/// ⚠️ <see cref="Enabled"/> 為 false 時，用量分析頁**整頁退回定價幣別**顯示。
/// 「有匯率的列顯示台幣、沒有的顯示美金」會讓總額變成兩種幣別相加，那不是降級，是錯的數字。
/// </para>
/// </summary>
public class ExchangeRateSettings : IValidatableObject
{
    public const string SectionName = "ExchangeRateSettings";

    /// <summary>是否啟用匯率換算。false 時完全不會有背景抓取，用量分析頁維持定價幣別顯示。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 匯率來源。預期回應形如 <c>{ "result": "success", "base_code": "USD", "rates": { "TWD": 31.86 } }</c>。
    /// 目前用 open.er-api.com（免金鑰、每日更新一次）。
    /// </summary>
    public string SourceUrl { get; set; } = "https://open.er-api.com/v6/latest/USD";

    /// <summary>換算目標幣別代碼（三碼），例如 TWD。</summary>
    public string TargetCurrency { get; set; } = "TWD";

    /// <summary>
    /// 重抓間隔（小時）。
    ///
    /// <para>
    /// ⚠️ 刻意用 <see cref="int"/> 而不是 <see cref="TimeSpan"/>：設定檔寫 <c>"24:00:00"</c> 時
    /// 綁定會失敗（TimeSpan 的小時欄只收 0–23，一天要寫成 <c>"1.00:00:00"</c>），
    /// 而且錯誤訊息完全看不出是這個原因。
    /// </para>
    /// </summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>
    /// 保底匯率。連一次都沒抓成功、資料庫裡也沒有任何歷史匯率時才會用到。
    ///
    /// <para>
    /// ⚠️ 這是<b>離線時的最後手段，會帶 1～3% 的系統性誤差而且畫面上看不出來</b>。
    /// 本專案一貫的規則是「算不出來回 null，絕不回一個看起來合理的錯數字」（見 <c>AiUsagePricing</c>），
    /// 所以留空其實比較誠實——代價是畫面上會出現一片「—」。
    /// 目前設定檔給了 32.0，是刻意拿一點準確度換可讀性。
    /// </para>
    /// </summary>
    public decimal? FallbackRate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // ⚠️ 這裡可以早退，但理由與 LlmSettings 正好相反，不要照抄那邊的結論：
        //    LlmSettings.Validate 特地把單價檢查放在最前面，因為它後面的分支有 yield break，
        //    放後面會讓「沒指定 TranscriptionProvider」的部署永遠檢查不到單價。
        //    這裡的 Enabled 是整段功能的總開關——關掉之後其他鍵一個都不會被讀，早退才是對的。
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(SourceUrl))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(SourceUrl)} 不可空白（或把 {nameof(Enabled)} 設為 false）。",
                [nameof(SourceUrl)]);
        }
        else if (!Uri.TryCreate(SourceUrl.Trim(), UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // 刻意不驗證「連得上」——外部服務暫時不可用不該擋住啟動，
            // 與 LlmSettings 不驗證 ApiKey 是同一個道理。
            yield return new ValidationResult(
                $"{SectionName}:{nameof(SourceUrl)} 必須是 http 或 https 的絕對網址（目前為「{SourceUrl}」）。",
                [nameof(SourceUrl)]);
        }

        // 「TWD 」這種尾隨空白的 typo 不擋的話，系統會完全正常啟動，然後永遠查不到匯率，
        // 只在 log 裡留一行 warning——這種錯最難被發現。
        var currency = TargetCurrency ?? string.Empty;
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(TargetCurrency)} 必須是三碼英文幣別代碼（目前為「{TargetCurrency}」）。",
                [nameof(TargetCurrency)]);
        }

        // 0 匯率會讓所有台幣金額變成 NT$0，在圖表上只是看起來「這個月比較省」。
        if (FallbackRate is <= 0)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(FallbackRate)} 必須大於 0（目前為 {FallbackRate}）。留空代表沒有保底匯率。",
                [nameof(FallbackRate)]);
        }

        // 0 會讓背景迴圈空轉；上限一週是為了擋住「輸入 8760 當成秒」這種單位誤會。
        if (RefreshIntervalHours is < 1 or > 168)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(RefreshIntervalHours)} 必須介於 1 到 168 小時（目前為 {RefreshIntervalHours}）。",
                [nameof(RefreshIntervalHours)]);
        }
    }
}
