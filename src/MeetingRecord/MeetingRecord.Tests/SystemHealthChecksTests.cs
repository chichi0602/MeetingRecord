using System.Reflection;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Caching;
using MeetingRecord.Web.Diagnostics;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Tests;

/// <summary>
/// 系統健康監控各項檢查的判斷邏輯（0.4.93 本系統功能六項；0.4.123 日誌、登入金鑰、快取服務）、權重合計，以及選單／權限註冊。
/// 例外紀錄管線見 <c>LoggingPipelineHealthTests</c>。
/// </summary>
public sealed class SystemHealthChecksTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 10, 0, 0);

    // ───────── 權重 ─────────

    [Fact]
    public void Weights_ShouldSumToExactly100()
    {
        // 加減檢查項目時忘了重配，總分的意義會悄悄改變（例如全部正常卻不是 100%）。
        var total = typeof(SystemHealthWeights)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Sum(f => (int)f.GetRawConstantValue()!);

        Assert.Equal(100, total);
    }

    // ───────── AI 供應商設定 ─────────

    [Fact]
    public void AiProvider_NotConfigured_ShouldBeUnhealthy()
    {
        var verdict = SystemHealthChecks.EvaluateAiProvider(new LlmSettings());

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DevelopmentOnly-ChangeThisLlmApiKey")]
    public void AiProvider_MissingOrDevelopmentKey_ShouldBeUnhealthy(string apiKey)
    {
        var settings = CreateLlmSettings();
        settings.Providers["AzureOpenAI"].ApiKey = apiKey;

        var verdict = SystemHealthChecks.EvaluateAiProvider(settings);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Fact]
    public void AiProvider_SampleEndpoint_ShouldBeUnhealthy()
    {
        var settings = CreateLlmSettings();
        settings.Providers["AzureOpenAI"].Endpoint = "https://your-resource.openai.azure.com/";

        Assert.Equal(SystemHealthStatus.Unhealthy, SystemHealthChecks.EvaluateAiProvider(settings).Status);
    }

    [Fact]
    public void AiProvider_ModelWithoutPricing_ShouldBeDegradedAndNameTheModel()
    {
        // 0.4.88 金額全變「—」的元兇：設定裡的模型沒有單價。
        var settings = CreateLlmSettings();
        settings.Pricing.Remove("gpt-4o-transcribe");

        var verdict = SystemHealthChecks.EvaluateAiProvider(settings);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
        Assert.Contains("gpt-4o-transcribe", verdict.FailureMessage);
    }

    [Fact]
    public void AiProvider_TextModelWithOnlyInputPrice_ShouldBeDegraded()
    {
        var settings = CreateLlmSettings();
        settings.Pricing["gpt-4o-mini"].OutputPerMillionTokens = null;

        Assert.Equal(SystemHealthStatus.Degraded, SystemHealthChecks.EvaluateAiProvider(settings).Status);
    }

    [Fact]
    public void AiProvider_AllConfigured_ShouldBeHealthyAndNeverLeakTheKey()
    {
        var settings = CreateLlmSettings();

        var verdict = SystemHealthChecks.EvaluateAiProvider(settings);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.DoesNotContain("real-secret-key", verdict.Evidence);
    }

    [Fact]
    public void AiProvider_ShippedPricing_ShouldCoverTheDeploymentsInUse()
    {
        // 0.4.124：Azure 轉錄部署名稱是 gpt-transcribe，出貨的 appsettings.json 沒有它的單價，
        // 健康監控亮黃燈、轉錄金額全變「—」。這裡以出貨的單價表為準，日後有人刪掉會被擋下。
        var settings = new LlmSettings();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(FindMenuJsonPath()))!, "appsettings.json"))
            .Build()
            .GetSection(LlmSettings.SectionName)
            .Bind(settings);

        var provider = settings.Providers["AzureOpenAI"];
        provider.Endpoint = "https://contoso.openai.azure.com/";
        provider.ApiKey = "real-secret-key";
        settings.DefaultProvider = "AzureOpenAI";
        settings.TranscriptionProvider = "AzureOpenAI";

        foreach (var (model, transcriptionModel) in new[] { ("gpt-5.6-sol", "gpt-transcribe"), ("gpt-4o-mini", "gpt-4o-transcribe") })
        {
            provider.Model = model;
            provider.TranscriptionModel = transcriptionModel;

            var verdict = SystemHealthChecks.EvaluateAiProvider(settings);

            Assert.True(verdict.Status == SystemHealthStatus.Healthy, $"{model}／{transcriptionModel}：{verdict.FailureMessage}");
        }

        Assert.Equal(0.0045m, settings.Pricing["gpt-transcribe"].AudioPerMinute);
    }

    // ───────── 語音轉錄 ─────────

    [Fact]
    public void Transcription_NotEnabled_ShouldNotDeductPoints()
    {
        var verdict = SystemHealthChecks.EvaluateTranscription(false, "", _ => false);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\nowhere\ffmpeg.exe")]
    public void Transcription_EnabledWithoutFfmpeg_ShouldBeUnhealthy(string path)
    {
        var verdict = SystemHealthChecks.EvaluateTranscription(true, path, _ => false);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Fact]
    public void Transcription_EnabledWithFfmpeg_ShouldBeHealthy()
    {
        Assert.Equal(SystemHealthStatus.Healthy, SystemHealthChecks.EvaluateTranscription(true, "ffmpeg", _ => true).Status);
    }

    // ───────── PDF 匯出 ─────────

    [Fact]
    public void PdfExport_NoBrowser_ShouldBeDegradedNotUnhealthy()
    {
        // 只影響下載 PDF 一個按鈕，不該讓整個系統亮紅燈。
        var verdict = SystemHealthChecks.EvaluatePdfExport("", _ => false);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void PdfExport_BrowserFound_ShouldBeHealthy()
    {
        Assert.Equal(SystemHealthStatus.Healthy, SystemHealthChecks.EvaluatePdfExport("", _ => true).Status);
    }

    // ───────── 匯率 ─────────

    [Fact]
    public void ExchangeRate_Disabled_ShouldNotDeductPoints()
    {
        var verdict = SystemHealthChecks.EvaluateExchangeRate(new ExchangeRateSettings { Enabled = false }, null, null, Now);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
    }

    [Fact]
    public void ExchangeRate_EnabledButNoRate_ShouldBeUnhealthy()
    {
        var verdict = SystemHealthChecks.EvaluateExchangeRate(CreateRateSettings(), null, Now.AddMinutes(-5), Now);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Theory]
    [InlineData(ExchangeRateSource.Ledger)]
    [InlineData(ExchangeRateSource.Fallback)]
    public void ExchangeRate_NotLive_ShouldBeDegradedEvenIfJustSet(ExchangeRateSource source)
    {
        // 種子與保底值的 RetrievedAt 是「放進快取的當下」，看起來剛取得，其實是舊值。
        var snapshot = new ExchangeRateSnapshot("USD", "TWD", 32m, Now, source);

        var verdict = SystemHealthChecks.EvaluateExchangeRate(CreateRateSettings(), snapshot, null, Now);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void ExchangeRate_LiveButOverdue_ShouldBeDegraded()
    {
        var snapshot = new ExchangeRateSnapshot("USD", "TWD", 32m, Now.AddHours(-26), ExchangeRateSource.Live);

        var verdict = SystemHealthChecks.EvaluateExchangeRate(CreateRateSettings(), snapshot, Now.AddHours(-1), Now);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void ExchangeRate_LiveWithinIntervalPlusGrace_ShouldBeHealthy()
    {
        var snapshot = new ExchangeRateSnapshot("USD", "TWD", 32m, Now.AddHours(-24.5), ExchangeRateSource.Live);

        var verdict = SystemHealthChecks.EvaluateExchangeRate(CreateRateSettings(), snapshot, null, Now);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
    }

    [Fact]
    public void ExchangeRateCache_MarkFailed_ShouldNotTouchCurrentRate()
    {
        var cache = new ExchangeRateCache();
        cache.Set(new ExchangeRateSnapshot("USD", "TWD", 32m, Now));

        cache.MarkFailed(Now);

        Assert.Equal(32m, cache.Current!.Rate);
        Assert.Equal(Now, cache.LastFailureAt);
    }

    // ───────── 背景工作 ─────────

    [Fact]
    public void FindStuckJobs_DatabaseSaysProcessingButQueueDoesNotHaveIt_ShouldBeStuck()
    {
        var jobs = new[] { new BackgroundJobRecord(BackgroundJobKind.Transcription, 7, "週會", true, Now.AddMinutes(-5)) };

        var stuck = SystemHealthChecks.FindStuckJobs(jobs, new HashSet<int>(), new HashSet<int>(), Now);

        Assert.Single(stuck);
        Assert.Contains("#7", stuck[0]);
    }

    [Fact]
    public void FindStuckJobs_RunningWithinThreshold_ShouldNotBeStuck()
    {
        var jobs = new[] { new BackgroundJobRecord(BackgroundJobKind.Transcription, 7, "週會", true, Now.AddHours(-1)) };

        var stuck = SystemHealthChecks.FindStuckJobs(jobs, new HashSet<int> { 7 }, new HashSet<int>(), Now);

        Assert.Empty(stuck);
    }

    [Fact]
    public void FindStuckJobs_RunningBeyondThreshold_ShouldBeStuck()
    {
        var jobs = new[] { new BackgroundJobRecord(BackgroundJobKind.MeetingDraft, 7, "週會", true, Now.AddHours(-4)) };

        var stuck = SystemHealthChecks.FindStuckJobs(jobs, new HashSet<int>(), new HashSet<int> { 7 }, Now);

        Assert.Single(stuck);
    }

    [Fact]
    public void FindStuckJobs_ShouldMatchAgainstTheQueueOfTheSameKind()
    {
        // 同一場會議的轉錄在跑，不代表它的草稿工作也有人在處理。
        var jobs = new[] { new BackgroundJobRecord(BackgroundJobKind.MeetingDraft, 7, "週會", false, null) };

        var stuck = SystemHealthChecks.FindStuckJobs(jobs, new HashSet<int> { 7 }, new HashSet<int>(), Now);

        Assert.Single(stuck);
    }

    [Fact]
    public void BackgroundJobs_WithStuckJob_ShouldBeDegraded()
    {
        Assert.Equal(SystemHealthStatus.Healthy, SystemHealthChecks.EvaluateBackgroundJobs(2, 1, []).Status);
        Assert.Equal(SystemHealthStatus.Degraded, SystemHealthChecks.EvaluateBackgroundJobs(0, 0, ["x"]).Status);
    }

    // ───────── 近期錯誤 ─────────

    [Theory]
    [InlineData(10, 0, 0, SystemHealthStatus.Healthy)]
    [InlineData(8, 2, 0, SystemHealthStatus.Healthy)]      // 20% 不算超過
    [InlineData(7, 3, 0, SystemHealthStatus.Degraded)]     // 30%
    [InlineData(4, 6, 0, SystemHealthStatus.Unhealthy)]    // 60%
    [InlineData(10, 0, 1, SystemHealthStatus.Degraded)]    // 近 24 小時有例外
    [InlineData(0, 1, 0, SystemHealthStatus.Degraded)]     // 樣本太少：1 次失敗不是 100% 紅燈
    [InlineData(0, 0, 0, SystemHealthStatus.Healthy)]
    public void RecentErrors_ShouldApplyThresholds(int succeeded, int failed, int exceptions, SystemHealthStatus expected)
    {
        Assert.Equal(expected, SystemHealthChecks.EvaluateRecentErrors(succeeded, failed, exceptions).Status);
    }

    [Fact]
    public void RecentErrors_WithExceptions_ShouldPointToExceptionLogPage()
    {
        // 0.4.123 起頁尾不再有原始日誌，提示要指向「系統例外紀錄」。
        var verdict = SystemHealthChecks.EvaluateRecentErrors(10, 0, 3);

        Assert.Contains("3 種", verdict.Evidence);
        Assert.Contains("系統例外紀錄", verdict.FailureMessage);
    }

    // ───────── 日誌（0.4.123） ─────────

    [Fact]
    public void Logging_BasePathMissing_ShouldBeUnhealthy()
    {
        var verdict = SystemHealthChecks.EvaluateLogging(string.Empty, false, string.Empty, null, LogLevelRank.Info, null);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
        Assert.Contains("NLog:BasePath", verdict.FailureMessage);
    }

    [Fact]
    public void Logging_DirectoryNotWritable_ShouldBeUnhealthy()
    {
        var verdict = SystemHealthChecks.EvaluateLogging(@"C:\logs", false, @"C:\logs\a.log", 10, LogLevelRank.Info, null);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
    }

    [Fact]
    public void Logging_TodayFileMissing_ShouldBeDegraded()
    {
        var verdict = SystemHealthChecks.EvaluateLogging(@"C:\logs", true, @"C:\logs\a.log", null, LogLevelRank.Info, null);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
        Assert.Contains("尚未建立", verdict.Evidence);
    }

    [Fact]
    public void Logging_Ok_ShouldShowEffectiveLevelAndOverride()
    {
        var verdict = SystemHealthChecks.EvaluateLogging(@"C:\logs", true, @"C:\logs\a.log", 2048, LogLevelRank.Debug, LogLevelRank.Debug);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Contains("DEBUG", verdict.Evidence);
        Assert.Contains("執行期覆寫", verdict.Evidence);
    }

    // ───────── 登入金鑰（0.4.123） ─────────

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData(@"C:\keys", false, false)]   // 目錄不存在
    [InlineData(@"C:\keys", true, false)]    // 不可寫
    public void DataProtection_PathMissingOrNotWritable_ShouldBeUnhealthy(string? path, bool exists, bool writable)
    {
        var verdict = SystemHealthChecks.EvaluateDataProtection(path, exists, writable, 1, 1, DateTimeOffset.Now.AddDays(80));

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
        Assert.Contains("重新登入", verdict.FailureMessage);
    }

    [Fact]
    public void DataProtection_NoActiveKey_ShouldBeDegraded()
    {
        var verdict = SystemHealthChecks.EvaluateDataProtection(@"C:\keys", true, true, 0, 0, null);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void DataProtection_ActiveKey_ShouldBeHealthyAndOnlyShowCounts()
    {
        var verdict = SystemHealthChecks.EvaluateDataProtection(@"C:\keys", true, true, 2, 1, new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Contains("金鑰檔：2 個", verdict.Evidence);
        Assert.Contains("有效金鑰：1 把", verdict.Evidence);
        Assert.DoesNotContain("<", verdict.Evidence);
    }

    // ───────── 快取服務（0.4.123） ─────────

    [Fact]
    public async Task Cache_RoundTripOk_ShouldBeHealthyAndRemoveSentinel()
    {
        var cache = new FakeCache();

        var verdict = await SystemHealthChecks.ProbeCacheAsync(cache, "Provider：Memory", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(SystemHealthStatus.Healthy, verdict.Status);
        Assert.Empty(cache.Values);
    }

    [Fact]
    public async Task Cache_ReadBackMismatch_ShouldBeDegraded()
    {
        var cache = new FakeCache { ReturnWrongValue = true };

        var verdict = await SystemHealthChecks.ProbeCacheAsync(cache, "Provider：Memory", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(SystemHealthStatus.Degraded, verdict.Status);
    }

    [Fact]
    public async Task Cache_Throws_ShouldBeUnhealthy()
    {
        var cache = new FakeCache { Throw = true };

        var verdict = await SystemHealthChecks.ProbeCacheAsync(cache, "Provider：Redis", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
        Assert.Contains("InvalidOperationException", verdict.FailureMessage);
    }

    [Fact]
    public async Task Cache_Hangs_ShouldTimeOutAsUnhealthy()
    {
        var cache = new FakeCache { Hang = true };

        var verdict = await SystemHealthChecks.ProbeCacheAsync(cache, "Provider：Redis", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(SystemHealthStatus.Unhealthy, verdict.Status);
        Assert.Contains("逾時", verdict.Evidence);
    }

    private sealed class FakeCache : ICacheService
    {
        public Dictionary<string, object?> Values { get; } = [];
        public bool ReturnWrongValue { get; init; }
        public bool Throw { get; init; }
        public bool Hang { get; init; }

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            await GuardAsync(cancellationToken);
            if (ReturnWrongValue)
            {
                return (T)(object)"wrong";
            }

            return Values.TryGetValue(key, out var value) ? (T?)value : default;
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        {
            await GuardAsync(cancellationToken);
            Values[key] = value;
        }

        public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            await GuardAsync(cancellationToken);
            Values.Remove(key);
        }

        private async Task GuardAsync(CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("redis down");
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    // ───────── 選單／權限註冊 ─────────

    [Fact]
    public void RolePermissionCatalog_ShouldNotExposeSystemHealth()
    {
        // 0.4.120 起只限管理者：列在權限目錄裡就能勾給角色。舊鍵「系統健康度」也不能回來。
        var keys = new RolePermissionService().GetRolePermissionAllName();

        Assert.DoesNotContain(MagicObjectHelper.角色_系統健康監控, keys);
        Assert.DoesNotContain(MagicObjectHelper.已退役權限鍵_系統健康度, keys);
    }

    [Fact]
    public void MenuJson_ShouldContainSystemHealthUnderMonitoringGroup()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var systemGroup = document.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 3);
        var monitoringGroup = systemGroup.GetProperty("subMenu").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 36);
        var node = monitoringGroup.GetProperty("subMenu").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 34);

        // name 是頁面標題（MainLayout 依 Menu.json 顯示），與常數一字不差。
        Assert.Equal(MagicObjectHelper.角色_系統健康監控, node.GetProperty("name").GetString());
        Assert.Equal("/system-health", node.GetProperty("url").GetString());
    }

    private static LlmSettings CreateLlmSettings()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            TranscriptionProvider = "AzureOpenAI",
        };
        settings.Providers["AzureOpenAI"] = new LlmProviderSettings
        {
            Endpoint = "https://contoso.openai.azure.com/",
            ApiKey = "real-secret-key",
            Model = "gpt-4o-mini",
            ApiVersion = "2024-10-21",
            TranscriptionModel = "gpt-4o-transcribe",
        };
        settings.Pricing["gpt-4o-mini"] = new LlmPricingSettings { InputPerMillionTokens = 0.15m, OutputPerMillionTokens = 0.60m };
        settings.Pricing["gpt-4o-transcribe"] = new LlmPricingSettings { AudioPerMinute = 0.006m };
        return settings;
    }

    private static ExchangeRateSettings CreateRateSettings() => new()
    {
        Enabled = true,
        TargetCurrency = "TWD",
        RefreshIntervalHours = 24,
    };

    private static string FindMenuJsonPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "MeetingRecord", "MeetingRecord.Web", "Datas", "Menu.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "MeetingRecord.Web", "Datas", "Menu.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 MeetingRecord.Web/Datas/Menu.json。");
    }
}
