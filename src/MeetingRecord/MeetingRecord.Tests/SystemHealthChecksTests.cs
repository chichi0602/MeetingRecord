using System.Reflection;
using System.Text.Json;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Tests;

/// <summary>
/// 系統健康度 0.4.93 新增的六項檢查的判斷邏輯、權重合計，以及選單／權限註冊。
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
    [InlineData(10, 0, 1, SystemHealthStatus.Degraded)]    // 日誌有 ERROR
    [InlineData(0, 1, 0, SystemHealthStatus.Degraded)]     // 樣本太少：1 次失敗不是 100% 紅燈
    [InlineData(0, 0, 0, SystemHealthStatus.Healthy)]
    public void RecentErrors_ShouldApplyThresholds(int succeeded, int failed, int logErrors, SystemHealthStatus expected)
    {
        Assert.Equal(expected, SystemHealthChecks.EvaluateRecentErrors(succeeded, failed, logErrors).Status);
    }

    [Theory]
    [InlineData("2026-09-22 10:00:00.1234|abc|ERROR|12|Some.Logger|boom|", true)]
    [InlineData("2026-09-22 10:00:00.1234|abc|FATAL|12|Some.Logger|boom|", true)]
    [InlineData("2026-09-22 10:00:00.1234|abc|INFO|12|Some.Logger|ERROR in message|", false)]
    [InlineData("   at Some.Method() in C:\\x.cs:line 3", false)]
    public void IsErrorLine_ShouldOnlyLookAtTheLevelColumn(string line, bool expected)
    {
        Assert.Equal(expected, HealthLogReader.IsErrorLine(line));
    }

    // ───────── 選單／權限註冊 ─────────

    [Fact]
    public void RolePermissionCatalog_ShouldExposeSystemHealthAsASingleSwitch()
    {
        var group = new RolePermissionService().GetRoleListPermissionAllName()
            .Single(x => x.Contains(MagicObjectHelper.角色_系統健康度));

        Assert.Single(group);
    }

    [Fact]
    public void MenuJson_ShouldContainSystemHealthUnderSystemAdministration()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindMenuJsonPath()));

        var systemGroup = document.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 3);
        var node = systemGroup.GetProperty("subMenu").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == 34);

        // ⚠️ name 必須與權限鍵一字不差，否則非管理員勾了權限也看不到。
        Assert.Equal(MagicObjectHelper.角色_系統健康度, node.GetProperty("name").GetString());
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
