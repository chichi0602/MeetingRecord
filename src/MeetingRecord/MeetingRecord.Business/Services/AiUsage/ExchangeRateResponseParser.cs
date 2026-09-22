using System.Text.Json;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 解析匯率來源的回應（0.4.88）。抽成純函式以便單元測試——與
/// <c>AzureOpenAiTextGenerationProvider.ExtractUsage</c>、<see cref="AiUsagePricing"/> 同一個慣例：
/// 網路呼叫留在 <see cref="ExchangeRateFetcher"/>，解析留在這裡，這樣離線也測得到每一個邊界。
///
/// <para>
/// 預期格式（open.er-api.com）：
/// <c>{ "result": "success", "base_code": "USD", "rates": { "TWD": 31.863639, ... } }</c>
/// </para>
///
/// <para>
/// ⚠️ <b>任何一種不如預期都回 null，絕不回 0、絕不擲例外。</b>
/// 0 匯率會讓所有台幣金額變成 NT$0，在圖表上只是看起來「這個月比較省」；
/// 而擲例外會讓背景服務為了一個匯率而中斷。
/// </para>
/// </summary>
public static class ExchangeRateResponseParser
{
    private const string SuccessResult = "success";

    /// <summary>
    /// 解析回應。
    /// </summary>
    /// <param name="json">回應原文。</param>
    /// <param name="expectedBaseCurrency">預期的來源幣別（＝定價幣別）。對不上一律回 null。</param>
    /// <param name="targetCurrency">要取哪一個幣別的匯率。</param>
    public static ExchangeRateSnapshot? Parse(string? json, string? expectedBaseCurrency, string? targetCurrency)
    {
        if (string.IsNullOrWhiteSpace(json)
            || string.IsNullOrWhiteSpace(expectedBaseCurrency)
            || string.IsNullOrWhiteSpace(targetCurrency))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 對方回了 HTML 錯誤頁、或連線被截斷。這是可預期的狀況，不是程式錯誤。
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // result 不是 success 時，rates 可能整個不存在，也可能是上一次的舊值。
            if (!root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.String
                || !string.Equals(result.GetString(), SuccessResult, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // ⚠️ base_code 一定要驗。設定裡的 SourceUrl 結尾寫著幣別（.../latest/USD），
            //    有人把它改成 EUR 卻沒改 LlmSettings.Currency 時，金額會安靜地錯 8%。
            if (!root.TryGetProperty("base_code", out var baseCode)
                || baseCode.ValueKind != JsonValueKind.String
                || !string.Equals(baseCode.GetString(), expectedBaseCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // ⚠️ JsonElement.TryGetProperty 是大小寫敏感的，而幣別代碼在設定檔裡可能被寫成小寫。
            //    這裡自己走一遍屬性做不分大小寫的比對。
            var target = targetCurrency.Trim();
            foreach (var rate in rates.EnumerateObject())
            {
                if (!string.Equals(rate.Name, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 字串形式的數字（"31.8"）不接受：那代表對方換了格式，此時「猜」比「不換算」危險。
                if (rate.Value.ValueKind != JsonValueKind.Number
                    || !rate.Value.TryGetDecimal(out var value)
                    || value <= 0)
                {
                    return null;
                }

                return new ExchangeRateSnapshot(
                    expectedBaseCurrency.Trim().ToUpperInvariant(),
                    target.ToUpperInvariant(),
                    value,
                    DateTime.Now);
            }

            return null;
        }
    }
}
