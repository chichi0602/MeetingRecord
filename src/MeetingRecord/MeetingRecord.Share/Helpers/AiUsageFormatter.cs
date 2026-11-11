using System.Globalization;

namespace MeetingRecord.Share.Helpers;

/// <summary>
/// Token 用量的顯示文字（1.1.37 從 Business 的 <c>AiUsageMetrics</c> 抽出，伺服器與 WASM 頁共用；
/// <c>AiUsageMetrics</c> 同名方法轉呼叫這裡）。純函式。
/// </summary>
public static class AiUsageFormatter
{
    /// <summary>
    /// 金額的顯示文字。單價沒設定（null）時是「—」而不是「$0.00」——
    /// 後者與「真的沒花錢」看起來一模一樣。
    /// </summary>
    public static string FormatAmount(decimal? amount, string? currency)
    {
        if (amount is not { } value)
        {
            return "—";
        }

        // 金額很小（一次問答可能是 0.000045）時，兩位小數會全部變成 0.00。
        var text = value >= 1m
            ? value.ToString("0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.000000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

        return string.IsNullOrWhiteSpace(text) ? "0" : $"{currency} {text}".Trim();
    }

    /// <summary>
    /// 換算後金額的顯示文字（0.4.88）。null（沒單價或沒匯率）時是「—」而不是「NT$ 0」——
    /// 後者與「真的沒花錢」看起來一模一樣。
    ///
    /// <para>
    /// ⚠️ 金額很小時不可四捨五入成 NT$0.00：一次 AI 問答大約是 NT$0.0014，
    /// 兩位小數會讓整張明細表看起來全部免費。
    /// </para>
    /// </summary>
    public static string FormatConverted(decimal? amount, string? currency)
    {
        if (amount is not { } value)
        {
            return "—";
        }

        var text = value >= 1m
            ? value.ToString("#,##0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.0000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "0";
        }

        // 台幣用習慣的符號，其他幣別就印代碼——不去猜每一種幣別的符號。
        return string.Equals(currency, "TWD", StringComparison.OrdinalIgnoreCase)
            ? $"NT$ {text}"
            : $"{currency} {text}".Trim();
    }

    /// <summary>token 數的顯示文字：上千縮寫成 K／M，清單才不會被一串數字撐爆。</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>音訊秒數的顯示文字。</summary>
    public static string FormatAudio(double seconds)
    {
        if (seconds <= 0)
        {
            return "0 分鐘";
        }

        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小時"
            : $"{span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} 分鐘";
    }
}
