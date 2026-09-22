using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 定期把美金→顯示幣別的匯率抓進 <see cref="ExchangeRateCache"/>（0.4.88）。
///
/// <para>
/// ⚠️ <b>抓匯率只能在這條背景軌道上。</b> 記帳路徑（<c>AiUsageRecorder</c>）跑在使用者等待中的
/// AI 呼叫裡，那裡只能讀 <see cref="ExchangeRateCache"/> 的同步欄位，不得 await 任何 HTTP。
/// 理由見 <see cref="ExchangeRateCache"/> 的說明。
/// </para>
/// </summary>
public sealed class ExchangeRateBackgroundService : BackgroundService
{
    /// <summary>
    /// 抓失敗後的重試間隔。刻意寫死不開設定：這是失敗處理的實作細節，
    /// 不是使用者會想調的東西（CLAUDE.md §2）。
    /// </summary>
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(10);

    private readonly ExchangeRateCache cache;
    private readonly IOptions<ExchangeRateSettings> settings;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<ExchangeRateBackgroundService> logger;

    public ExchangeRateBackgroundService(
        ExchangeRateCache cache,
        IOptions<ExchangeRateSettings> settings,
        IOptions<LlmSettings> llmSettings,
        IServiceScopeFactory scopeFactory,
        ILogger<ExchangeRateBackgroundService> logger)
    {
        this.cache = cache;
        this.settings = settings;
        this.llmSettings = llmSettings;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (!options.Enabled)
        {
            // 直接結束，不要進迴圈空轉。停用時用量分析頁整頁維持定價幣別顯示。
            logger.LogInformation("Exchange rate conversion is disabled; background refresh will not run.");
            return;
        }

        // 冷啟動種子：先用資料庫裡最後一筆已知匯率頂著，免得重啟後到第一次抓成功之前
        // 那幾秒的紀錄完全沒有匯率。
        await SeedFromLedgerAsync(stoppingToken);

        // ⚠️ 進迴圈前一定要先抓一次。PeriodicTimer 的第一個 tick 要等一個完整間隔才來，
        //    只靠它的話重啟當天整整一天都不會有新匯率。
        var interval = TimeSpan.FromHours(options.RefreshIntervalHours);
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(succeeded ? interval : FailureRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Fetcher 是 Scoped（跟著 IHttpClientFactory 的慣例走），所以每次自建 scope。
            using var scope = scopeFactory.CreateScope();
            var fetcher = scope.ServiceProvider.GetRequiredService<ExchangeRateFetcher>();

            var snapshot = await fetcher.FetchAsync(cancellationToken);
            if (snapshot is null)
            {
                cache.MarkFailed(DateTime.Now);
                return false;
            }

            cache.Set(snapshot);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Fetcher 已經吞掉可預期的失敗；能走到這裡的是 DI 解析之類的意外。
            // 背景服務不可以因為一個匯率就死掉。
            logger.LogError(ex, "Exchange rate refresh failed unexpectedly.");
            cache.MarkFailed(DateTime.Now);
            return false;
        }
    }

    /// <summary>
    /// 從用量帳本撈最後一筆已知匯率當種子。
    ///
    /// <para>
    /// 「上次成功的匯率」不需要另開一張表或寫檔——<c>AiUsageLog.ExchangeRate</c> 本身
    /// 就是一份天然的匯率歷史，最後一列就是最後已知匯率。
    /// </para>
    /// </summary>
    private async Task SeedFromLedgerAsync(CancellationToken cancellationToken)
    {
        var options = settings.Value;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();

            // ⚠️ 只能用 IS NOT NULL 篩選並依 OccurredAt 排序。
            //    ExchangeRate 在 SQLite 是 TEXT，OrderBy 它會變成字典序（"10" < "9"）。
            var last = await context.AiUsageLog
                .AsNoTracking()
                .Where(x => x.ExchangeRate != null)
                .OrderByDescending(x => x.OccurredAt)
                .Select(x => new { x.ExchangeRate, x.Currency, x.ConvertedCurrency })
                .FirstOrDefaultAsync(cancellationToken);

            if (last?.ExchangeRate is not { } rate || rate <= 0)
            {
                SeedFromFallback();
                return;
            }

            // 改過幣別的部署不可以拿舊幣別的匯率當種子——那會錯得很離譜而且看不出來。
            var baseCurrency = llmSettings.Value.Currency;
            if (!string.Equals(last.Currency, baseCurrency, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(last.ConvertedCurrency, options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            {
                SeedFromFallback();
                return;
            }

            cache.Set(new ExchangeRateSnapshot(
                baseCurrency.Trim().ToUpperInvariant(),
                options.TargetCurrency.Trim().ToUpperInvariant(),
                rate,
                DateTime.Now,
                ExchangeRateSource.Ledger));

            logger.LogInformation("Seeded exchange rate from the usage ledger. 1 {Base} = {Rate} {Target}", last.Currency, rate, last.ConvertedCurrency);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Seeding the exchange rate from the usage ledger failed; falling back.");
            SeedFromFallback();
        }
    }

    private void SeedFromFallback()
    {
        var options = settings.Value;
        if (options.FallbackRate is not { } fallback || fallback <= 0)
        {
            // 沒有保底值是刻意的預設：沒有匯率就不換算，畫面顯示「—」。
            // 硬塞一個看起來合理的數字會產生永遠不會被發現的系統性誤差。
            return;
        }

        cache.Set(new ExchangeRateSnapshot(
            llmSettings.Value.Currency.Trim().ToUpperInvariant(),
            options.TargetCurrency.Trim().ToUpperInvariant(),
            fallback,
            DateTime.Now,
            ExchangeRateSource.Fallback));

        logger.LogWarning("Using the configured fallback exchange rate {Rate}; no live rate is available yet.", fallback);
    }
}
