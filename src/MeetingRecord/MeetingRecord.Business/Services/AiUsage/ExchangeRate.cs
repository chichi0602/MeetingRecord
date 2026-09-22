namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 某一刻的匯率（0.4.88）：1 單位 <paramref name="BaseCurrency"/> 可換多少 <paramref name="TargetCurrency"/>。
/// </summary>
/// <param name="BaseCurrency">來源幣別，必須與 <c>LlmSettings.Currency</c>（定價幣別）相同才會被採用。</param>
/// <param name="TargetCurrency">換算目標幣別。</param>
/// <param name="Rate">匯率，一律大於 0（解析端已經擋掉 0 與負數）。</param>
/// <param name="RetrievedAt">放進快取的時間，用於 log、畫面說明文字與健康檢查判斷是否逾期未更新。</param>
/// <param name="Source">
/// 這個匯率從哪裡來（0.4.93）。帳本種子與保底值的 <paramref name="RetrievedAt"/> 也是「放進快取的當下」，
/// 單看時間分不出「剛抓到」還是「剛啟動拿舊值頂著」，健康檢查要靠這個欄位區分。
/// </param>
public sealed record ExchangeRateSnapshot(
    string BaseCurrency,
    string TargetCurrency,
    decimal Rate,
    DateTime RetrievedAt,
    ExchangeRateSource Source = ExchangeRateSource.Live);

/// <summary>匯率來源（0.4.93）。</summary>
public enum ExchangeRateSource
{
    /// <summary>從匯率服務即時抓到的。</summary>
    Live = 0,

    /// <summary>啟動時從用量帳本最後一筆已知匯率拿來頂著的。</summary>
    Ledger = 1,

    /// <summary>設定檔的保底值（<c>ExchangeRateSettings.FallbackRate</c>）。</summary>
    Fallback = 2,
}

/// <summary>
/// 目前生效的匯率。<b>Singleton</b>：背景服務寫、Blazor circuit 與背景工作的 scope 讀，
/// 必須是同一個實例（理由與 <c>AiChatStore</c>、<c>IJobCancellationRegistry</c> 相同）。
///
/// <para>
/// ⚠️ <b>這個類別刻意不提供任何 async 方法。</b> 讀匯率的人是 <c>AiUsageRecorder</c>，
/// 而它是在使用者按下「產生會議紀錄」之後、串流結束之前被呼叫的。
/// 「過期才抓」的 lazy cache（<c>await GetRateAsync()</c>）平常瞬間回傳、通過測試也通過 review，
/// 然後某天匯率服務不回應（TCP 沒有 RST、只是不回），那一次記帳就卡在 HTTP 逾時上——
/// 卡住的位置正好是使用者等在畫面前面的地方。抓取一律在背景軌道，這裡只有一個欄位讀取。
/// </para>
///
/// <para>
/// 刻意做成具體類別而不是介面：只有一個實作，測試直接 <c>new</c> 再 <see cref="Set"/> 即可（CLAUDE.md §2）。
/// </para>
/// </summary>
public sealed class ExchangeRateCache
{
    // volatile：寫入來自背景執行緒、讀取來自其他執行緒。
    // 參考型別的指派本身是原子的，volatile 保證的是「寫完之後別人讀得到」。
    private volatile ExchangeRateSnapshot? current;

    /// <summary>目前已知的匯率。從來沒抓成功過就是 null——呼叫端必須能接受 null。</summary>
    public ExchangeRateSnapshot? Current => current;

    /// <summary>更新目前匯率。只有背景服務會呼叫。</summary>
    public void Set(ExchangeRateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        current = snapshot;
    }

    // DateTime 是 struct，無法 volatile；用 ticks（long）搭配 Volatile 讀寫，0 代表從未失敗。
    private long lastFailureTicks;

    /// <summary>最近一次抓匯率失敗的時間（0.4.93，健康檢查用）；從未失敗為 null。</summary>
    public DateTime? LastFailureAt
    {
        get
        {
            var ticks = Volatile.Read(ref lastFailureTicks);
            return ticks == 0 ? null : new DateTime(ticks);
        }
    }

    /// <summary>記下一次抓取失敗。只有背景服務會呼叫；不影響 <see cref="Current"/>。</summary>
    public void MarkFailed(DateTime at) => Volatile.Write(ref lastFailureTicks, at.Ticks);
}
