using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;
using System.Text.Json;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 去匯率來源抓一次匯率（0.4.88）。只被 <c>ExchangeRateBackgroundService</c> 呼叫——
/// <b>絕不可以在任何使用者等待的路徑上呼叫</b>，理由見 <see cref="ExchangeRateCache"/>。
///
/// <para>
/// ⚠️ 失敗一律吞掉並記 log，回 null。抓不到匯率只是「這段期間的紀錄沒有台幣金額」，
/// 不該讓背景服務中斷，更不該影響任何使用者的工作。
/// </para>
/// </summary>
public sealed class ExchangeRateFetcher
{
    /// <summary>具名 HttpClient 的名稱，註冊處見 <c>ServiceCollectionExtensions.AddExchangeRateServices</c>。</summary>
    public const string HttpClientName = "ExchangeRate";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<ExchangeRateSettings> settings;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<ExchangeRateFetcher> logger;

    public ExchangeRateFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<ExchangeRateSettings> settings,
        IOptions<LlmSettings> llmSettings,
        ILogger<ExchangeRateFetcher> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.settings = settings;
        this.llmSettings = llmSettings;
        this.logger = logger;
    }

    /// <summary>抓一次。成功回快照，任何失敗回 null（不擲例外）。</summary>
    public async Task<ExchangeRateSnapshot?> FetchAsync(CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var baseCurrency = llmSettings.Value.Currency;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var body = await client.GetStringAsync(options.SourceUrl.Trim(), cancellationToken);

            var snapshot = ExchangeRateResponseParser.Parse(body, baseCurrency, options.TargetCurrency);
            if (snapshot is null)
            {
                logger.LogWarning(
                    "Exchange rate response was not usable. SourceUrl={SourceUrl}, Base={Base}, Target={Target}",
                    options.SourceUrl,
                    baseCurrency,
                    options.TargetCurrency);
                return null;
            }

            logger.LogInformation(
                "Exchange rate updated. 1 {Base} = {Rate} {Target}",
                snapshot.BaseCurrency,
                snapshot.Rate,
                snapshot.TargetCurrency);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服務正在關閉，不是錯誤。
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            // TaskCanceledException 在這裡代表逾時（權杖沒被取消，見上面的 when）。
            logger.LogWarning(ex, "Exchange rate fetch failed. SourceUrl={SourceUrl}", options.SourceUrl);
            return null;
        }
    }
}
