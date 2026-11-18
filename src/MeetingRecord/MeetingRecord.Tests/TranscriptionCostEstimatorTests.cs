using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Dtos.AiUsage;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.68 成本閘門：轉錄預估費用與「預估超過本月剩餘額度」的判斷。
/// 估不出來或額度查不到時一律放行——閘門壞掉不能變成整個轉錄不能用。
/// </summary>
public sealed class TranscriptionCostEstimatorTests
{
    private const string Model = "gpt-4o-transcribe";

    [Fact]
    public void EstimateTwd_ShouldUseAudioPriceAndExchangeRate()
    {
        // 60 分鐘 × 0.006 USD × 32 = 11.52 TWD。
        var estimator = Create();

        Assert.Equal(11.52m, estimator.EstimateTwd(3600));
    }

    [Fact]
    public void EstimateTwd_ShouldBeNull_WithoutPriceOrRate()
    {
        Assert.Null(Create(price: null).EstimateTwd(3600));
        Assert.Null(Create(rate: null).EstimateTwd(3600));

        // 匯率幣別和定價幣別不同時不採用（同 AiUsageRecorder）。
        Assert.Null(Create(rateBase: "EUR").EstimateTwd(3600));
    }

    [Fact]
    public async Task GetRemainingTwdAsync_ShouldTakeTheSmallerLimitedScope_AndBeNullWhenUnlimited()
    {
        var quota = new StatusAiQuotaService(
            Status(AiQuotaScope.Global, limit: 1000, used: 200),
            Status(AiQuotaScope.User, limit: 300, used: 250));

        Assert.Equal(50m, await Create(quota: quota, quotaSettings: new AiQuotaSettings { GlobalMonthlyTwd = 1000, PerUserMonthlyTwd = 300 }).GetRemainingTwdAsync(7));
        Assert.Null(await Create(quota: quota, quotaSettings: new AiQuotaSettings()).GetRemainingTwdAsync(7));
    }

    [Fact]
    public async Task CheckAffordableAsync_ShouldBlockOnlyWhenEstimateExceedsRemaining()
    {
        var settings = new AiQuotaSettings { GlobalMonthlyTwd = 100 };

        // 剩 5 元，一小時約 11.52 元：擋。
        var tight = Create(quota: new StatusAiQuotaService(Status(AiQuotaScope.Global, 100, 95)), quotaSettings: settings);
        var message = await tight.CheckAffordableAsync(3600, userId: null);
        Assert.NotNull(message);
        Assert.Contains("NT$12", message);
        Assert.Contains("NT$5", message);

        // 剩 50 元：放行。
        var roomy = Create(quota: new StatusAiQuotaService(Status(AiQuotaScope.Global, 100, 50)), quotaSettings: settings);
        Assert.Null(await roomy.CheckAffordableAsync(3600, userId: null));

        // 估不出金額（沒單價）：放行。
        var noPrice = Create(price: null, quota: new StatusAiQuotaService(Status(AiQuotaScope.Global, 100, 99)), quotaSettings: settings);
        Assert.Null(await noPrice.CheckAffordableAsync(3600, userId: null));
    }

    [Fact]
    public async Task GetRemainingTwdAsync_ShouldAllow_WhenQuotaLookupThrows()
    {
        var estimator = Create(quota: new StatusAiQuotaService { Throw = true }, quotaSettings: new AiQuotaSettings { GlobalMonthlyTwd = 100 });

        Assert.Null(await estimator.GetRemainingTwdAsync(1));
        Assert.Null(await estimator.CheckAffordableAsync(36000, 1));
    }

    private static TranscriptionCostEstimator Create(
        decimal? price = 0.006m,
        decimal? rate = 32m,
        string rateBase = "USD",
        IAiQuotaService? quota = null,
        AiQuotaSettings? quotaSettings = null)
    {
        var llm = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings { Model = "gpt-4o-mini", TranscriptionModel = Model },
            },
        };

        if (price is { } perMinute)
        {
            llm.Pricing[Model] = new LlmPricingSettings { AudioPerMinute = perMinute };
        }

        var rates = new ExchangeRateCache();
        if (rate is { } value)
        {
            rates.Set(new ExchangeRateSnapshot(rateBase, "TWD", value, DateTime.Now));
        }

        return new TranscriptionCostEstimator(
            Options.Create(llm),
            rates,
            quota ?? new StatusAiQuotaService(),
            new StaticOptionsMonitor<AiQuotaSettings>(quotaSettings ?? new AiQuotaSettings()),
            NullLogger<TranscriptionCostEstimator>.Instance);
    }

    private static AiQuotaStatus Status(AiQuotaScope scope, int limit, decimal used)
        => new(scope, limit, used, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));
}
