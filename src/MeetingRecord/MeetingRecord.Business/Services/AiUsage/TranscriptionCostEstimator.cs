using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 轉錄的成本閘門（1.1.68，對標決策清單 D20）：依時長預估轉錄費用（新台幣），並和本月剩餘額度比較。
///
/// <para>
/// 兩個用途：
/// <list type="bullet">
/// <item>畫面在上傳／重新轉錄前顯示預估金額；超過 <see cref="AiQuotaSettings.TranscriptionConfirmTwd"/> 要使用者再確認（只提醒，不擋）。</item>
/// <item>轉錄工作在本機轉檔量到長度之後、送出第一段之前，用 <see cref="CheckAffordableAsync"/> 擋下「預估超過剩餘額度」的工作——這時還沒花錢。</item>
/// </list>
/// </para>
///
/// <para>
/// 估不出來（沒單價、沒匯率、幣別對不上）一律回 null 並放行：和 <c>AiQuotaService.CheckAsync</c> 查不到時放行一致，
/// 閘門壞掉不能變成整個轉錄不能用。預估以秒按比例計，實際帳單可能以分鐘進位而略高。
/// </para>
/// </summary>
public sealed class TranscriptionCostEstimator(
    IOptions<LlmSettings> llmSettings,
    ExchangeRateCache exchangeRates,
    IAiQuotaService quotaService,
    IOptionsMonitor<AiQuotaSettings> quotaSettings,
    ILogger<TranscriptionCostEstimator> logger)
{
    /// <summary>超過這個金額（新台幣）畫面要再確認；0＝不提醒。</summary>
    public int ConfirmThresholdTwd => quotaSettings.CurrentValue.TranscriptionConfirmTwd;

    /// <summary>預估轉錄費用（新台幣）。估不出來回 null。</summary>
    public decimal? EstimateTwd(double seconds)
    {
        var settings = llmSettings.Value;
        var model = settings.GetTranscriptionProvider()?.TranscriptionModel;
        if (string.IsNullOrWhiteSpace(model) || !settings.Pricing.TryGetValue(model, out var price))
        {
            return null;
        }

        // 匯率的幣別要和定價幣別相同才採用（同 AiUsageRecorder）。
        var rate = exchangeRates.Current;
        if (rate is null || !string.Equals(rate.BaseCurrency, settings.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return AiUsageExchange.ToTargetCurrency(AiUsagePricing.EstimateAudioCost(seconds, price), rate.Rate);
    }

    /// <summary>
    /// 本月剩餘額度（新台幣）：全系統與這位使用者兩者中較少的那個，用完是 0。都不限制、或查詢失敗回 null。
    /// </summary>
    public async Task<decimal?> GetRemainingTwdAsync(int? userId, CancellationToken cancellationToken = default)
    {
        var current = quotaSettings.CurrentValue;
        if (current.GlobalMonthlyTwd <= 0 && current.PerUserMonthlyTwd <= 0)
        {
            return null;
        }

        try
        {
            var limited = (await quotaService.GetStatusAsync(userId, cancellationToken)).Where(x => x.IsLimited).ToList();
            return limited.Count == 0 ? null : limited.Min(x => Math.Max(0m, x.Limit - x.Used));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Remaining AI quota lookup failed; the transcription is allowed. UserId={UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// 預估費用超過本月剩餘額度時回傳要寫進失敗原因的訊息；付得起（或估不出來）回 null。
    /// </summary>
    public async Task<string?> CheckAffordableAsync(double seconds, int? userId, CancellationToken cancellationToken = default)
    {
        if (EstimateTwd(seconds) is not { } estimated)
        {
            return null;
        }

        if (await GetRemainingTwdAsync(userId, cancellationToken) is not { } remaining || estimated <= remaining)
        {
            return null;
        }

        return $"預估轉錄費用約 NT${Math.Ceiling(estimated):0}，超過本月剩餘 AI 額度 NT${Math.Floor(remaining):0}，"
            + "沒有送出轉錄、也沒有產生費用。請洽管理者調整 AI 用量上限。";
    }
}
