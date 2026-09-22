using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Web.Health;

/// <summary>單一檢查項目的判斷結果：燈號、證據、失敗訊息。</summary>
public sealed record SystemHealthVerdict(SystemHealthStatus Status, string Evidence, string? FailureMessage = null);

/// <summary>資料庫裡一筆「待處理／處理中」的背景工作。<see cref="BackgroundJobKind"/> 沿用取消登錄表的那個列舉。</summary>
public sealed record BackgroundJobRecord(BackgroundJobKind Kind, int MeetingId, string Title, bool IsProcessing, DateTime? StartedAt);

/// <summary>
/// 各檢查項目的權重（0.4.93 重新分配）。合計必須恰好 100，由 <c>SystemHealthChecksTests</c> 守住——
/// 日後加減項目時忘了重配，總分的意義就會悄悄改變。
/// </summary>
public static class SystemHealthWeights
{
    // 基礎設施（68）
    public const int Application = 5;
    public const int Api = 5;
    public const int Database = 20;
    public const int Logging = 8;
    public const int Authentication = 10;
    public const int FileSystem = 10;
    public const int Host = 5;
    public const int Security = 5;

    // 本系統功能（32）
    public const int AiProvider = 10;
    public const int Transcription = 6;
    public const int BackgroundJobs = 6;
    public const int RecentErrors = 4;
    public const int PdfExport = 3;
    public const int ExchangeRate = 3;
}

/// <summary>
/// 本系統專屬檢查項目的判斷邏輯（0.4.93）。抽成純函式是為了能直接測——專案沒有 bUnit，
/// <see cref="SystemHealthService"/> 只負責蒐集輸入。
///
/// <para>
/// ⚠️ <b>這裡刻意沒有任何「實際打一次 AI」的檢查。</b> 程式裡能打的兩個 Azure 端點（chat、transcriptions）
/// 都會計費，而健康頁每次打開、每按一次重新整理都會跑一輪（開發慣例 §6.3）。AI 只看設定。
/// </para>
/// </summary>
public static class SystemHealthChecks
{
    /// <summary>處理中超過這麼久就視為卡住。三小時的會議錄音轉錄也遠低於這個值。</summary>
    public static readonly TimeSpan StuckJobThreshold = TimeSpan.FromHours(3);

    /// <summary>匯率逾期的緩衝：更新間隔之外再多給這麼久，避免剛好在排程邊緣時誤報。</summary>
    public static readonly TimeSpan ExchangeRateGracePeriod = TimeSpan.FromHours(1);

    /// <summary>AI 呼叫筆數少於這個值時不看失敗率（1 筆失敗就是 100%，沒有意義），只要有失敗就黃燈。</summary>
    public const int MinimumCallsForFailureRate = 5;

    public static SystemHealthVerdict EvaluateAiProvider(LlmSettings settings)
    {
        if (!settings.IsConfigured)
        {
            return new(
                SystemHealthStatus.Unhealthy,
                "LlmSettings:DefaultProvider 未設定。",
                "沒有設定 AI 供應商，AI 轉會議紀錄、抽出待辦、AI 問答都無法使用。");
        }

        var providerName = settings.DefaultProvider.Trim();
        if (!settings.Providers.TryGetValue(providerName, out var provider))
        {
            return new(
                SystemHealthStatus.Unhealthy,
                $"DefaultProvider：{providerName}。",
                $"LlmSettings:Providers 裡找不到「{providerName}」。");
        }

        var transcriptionModel = settings.IsTranscriptionConfigured
            && settings.Providers.TryGetValue(settings.EffectiveTranscriptionProviderName, out var transcriptionProvider)
                ? transcriptionProvider.TranscriptionModel.Trim()
                : null;

        // 單價只看「正在用的模型」：生成要輸入與輸出單價，轉錄要每分鐘單價。
        var missingPricing = new List<string>();
        if (!HasTextPricing(settings, provider.Model))
        {
            missingPricing.Add(provider.Model);
        }

        if (transcriptionModel is not null && !HasAudioPricing(settings, transcriptionModel))
        {
            missingPricing.Add(transcriptionModel);
        }

        // ⚠️ 證據欄會顯示在頁面上：只能寫「已設定／未設定」，絕不能帶出金鑰本身。
        var evidence = $"供應商：{providerName}；生成模型：{provider.Model}；轉錄模型：{transcriptionModel ?? "未啟用"}；"
            + $"金鑰：{(string.IsNullOrWhiteSpace(provider.ApiKey) ? "未設定" : "已設定")}；"
            + $"單價：{(missingPricing.Count == 0 ? "齊全" : "缺 " + string.Join("、", missingPricing))}。";

        if (string.IsNullOrWhiteSpace(provider.ApiKey)
            || string.Equals(provider.ApiKey, StartupSafetyValidator.DevelopmentLlmApiKey, StringComparison.Ordinal))
        {
            return new(SystemHealthStatus.Unhealthy, evidence, "API 金鑰未設定或仍是開發預設值，所有 AI 呼叫都會失敗。");
        }

        if (string.IsNullOrWhiteSpace(provider.Endpoint)
            || provider.Endpoint.Contains(StartupSafetyValidator.DevelopmentLlmEndpointMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new(SystemHealthStatus.Unhealthy, evidence, "Endpoint 未設定或仍是範例值，所有 AI 呼叫都會失敗。");
        }

        if (missingPricing.Count > 0)
        {
            return new(
                SystemHealthStatus.Degraded,
                evidence,
                $"以下模型沒有設定單價（LlmSettings:Pricing），AI 用量分析的金額會顯示「—」：{string.Join("、", missingPricing)}。");
        }

        return new(SystemHealthStatus.Healthy, evidence);
    }

    public static SystemHealthVerdict EvaluateTranscription(bool transcriptionConfigured, string? ffmpegPath, Func<string, bool> ffmpegExists)
    {
        if (!transcriptionConfigured)
        {
            // 沒啟用轉錄是合法的部署方式，不扣分；缺設定這件事由「AI 供應商設定」那一項反映。
            return new(SystemHealthStatus.Healthy, "未啟用語音轉錄，不檢查 FFmpeg。");
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return new(SystemHealthStatus.Unhealthy, "MediaSettings:FfmpegPath 未設定。", "轉錄前必須先用 FFmpeg 轉檔，沒有 FFmpeg 所有上傳的錄音都會轉錄失敗。");
        }

        return ffmpegExists(ffmpegPath)
            ? new(SystemHealthStatus.Healthy, $"FFmpeg：{ffmpegPath}（找得到執行檔）。")
            : new(SystemHealthStatus.Unhealthy, $"FFmpeg：{ffmpegPath}（找不到執行檔）。", "找不到 FFmpeg，所有上傳的錄音都會轉錄失敗。");
    }

    public static SystemHealthVerdict EvaluatePdfExport(string? configuredBrowserPath, Func<string, bool>? exists = null)
    {
        var resolved = BrowserPathResolver.Resolve(configuredBrowserPath, exists);
        if (resolved is not null)
        {
            return new(SystemHealthStatus.Healthy, $"產生 PDF 用的瀏覽器：{resolved}。");
        }

        // 只影響「下載 PDF」一個按鈕，其他功能照常，所以是黃燈不是紅燈。
        return new(
            SystemHealthStatus.Degraded,
            string.IsNullOrWhiteSpace(configuredBrowserPath)
                ? "ExportSettings:BrowserPath 未設定，常見安裝位置也找不到 Edge／Chrome。"
                : $"ExportSettings:BrowserPath：{configuredBrowserPath}（找不到執行檔）。",
            "找不到可用的瀏覽器，會議紀錄的「下載 PDF」會失敗。");
    }

    public static SystemHealthVerdict EvaluateExchangeRate(
        ExchangeRateSettings settings,
        ExchangeRateSnapshot? current,
        DateTime? lastFailureAt,
        DateTime now)
    {
        if (!settings.Enabled)
        {
            return new(SystemHealthStatus.Healthy, "未啟用匯率換算，AI 用量金額以定價幣別顯示。");
        }

        var failure = lastFailureAt is { } failedAt ? $"；最近一次抓取失敗：{failedAt:yyyy/MM/dd HH:mm}" : string.Empty;

        if (current is null)
        {
            return new(
                SystemHealthStatus.Unhealthy,
                $"目標幣別：{settings.TargetCurrency}；目前沒有任何匯率{failure}。",
                "抓不到即時匯率、帳本裡沒有舊匯率、也沒有設定保底值，AI 用量分析的金額會顯示「—」。");
        }

        var evidence = $"1 {current.BaseCurrency} = {current.Rate} {current.TargetCurrency}；"
            + $"來源：{DescribeSource(current.Source)}；取得時間：{current.RetrievedAt:yyyy/MM/dd HH:mm}{failure}。";

        if (current.Source != ExchangeRateSource.Live)
        {
            return new(
                SystemHealthStatus.Degraded,
                evidence,
                $"還沒抓到即時匯率，目前用的是{DescribeSource(current.Source)}。新的 AI 用量會用這個匯率換算。");
        }

        var dueBy = current.RetrievedAt + TimeSpan.FromHours(settings.RefreshIntervalHours) + ExchangeRateGracePeriod;
        if (now > dueBy)
        {
            return new(
                SystemHealthStatus.Degraded,
                evidence,
                $"匯率已超過 {settings.RefreshIntervalHours} 小時沒有更新成功，換算仍會繼續用這個舊匯率。");
        }

        return new(SystemHealthStatus.Healthy, evidence);
    }

    /// <summary>
    /// 找出卡住的背景工作，回傳每一件的說明文字。
    ///
    /// <para>
    /// 判斷依據是「資料庫說在處理，但實際上沒有人在處理」：背景佇列不持久化，工作只存在記憶體裡的
    /// 進度通知器；資料庫是「待處理／處理中」、通知器裡卻找不到進行中的項目，就代表那件事再也不會動了。
    /// 處理中超過 <see cref="StuckJobThreshold"/> 也算（例如卡在沒有回應的 HTTP 上）。
    /// </para>
    /// </summary>
    /// <param name="runningTranscriptions">轉錄通知器裡進行中的會議 Id。</param>
    /// <param name="runningDrafts">草稿通知器裡進行中的會議 Id。</param>
    public static IReadOnlyList<string> FindStuckJobs(
        IEnumerable<BackgroundJobRecord> jobs,
        IReadOnlySet<int> runningTranscriptions,
        IReadOnlySet<int> runningDrafts,
        DateTime now)
    {
        var stuck = new List<string>();
        foreach (var job in jobs)
        {
            var running = job.Kind == BackgroundJobKind.Transcription ? runningTranscriptions : runningDrafts;
            var kind = job.Kind == BackgroundJobKind.Transcription ? "轉錄" : "產生會議紀錄";

            if (!running.Contains(job.MeetingId))
            {
                stuck.Add($"「{job.Title}」{kind}（#{job.MeetingId}）：資料庫顯示{(job.IsProcessing ? "處理中" : "待處理")}，但背景佇列裡沒有這件工作");
            }
            else if (job.IsProcessing && job.StartedAt is { } startedAt && now - startedAt > StuckJobThreshold)
            {
                stuck.Add($"「{job.Title}」{kind}（#{job.MeetingId}）：已處理 {(now - startedAt).TotalHours:0.#} 小時");
            }
        }

        return stuck;
    }

    public static SystemHealthVerdict EvaluateBackgroundJobs(int queuedTranscriptions, int queuedDrafts, IReadOnlyList<string> stuckJobs)
    {
        var evidence = $"進行中／排隊中：轉錄 {queuedTranscriptions} 件、產生會議紀錄 {queuedDrafts} 件；卡住：{stuckJobs.Count} 件。";
        if (stuckJobs.Count == 0)
        {
            return new(SystemHealthStatus.Healthy, evidence);
        }

        return new(
            SystemHealthStatus.Degraded,
            evidence,
            $"{string.Join("；", stuckJobs)}。請到該會議取消後重新執行。");
    }

    /// <param name="aiSucceeded">近 24 小時成功的 AI 呼叫數。</param>
    /// <param name="aiFailed">近 24 小時失敗的 AI 呼叫數（呼叫端已排除使用者主動取消）。</param>
    /// <param name="logErrorCount">今日日誌 ERROR／FATAL 筆數。</param>
    public static SystemHealthVerdict EvaluateRecentErrors(int aiSucceeded, int aiFailed, int logErrorCount)
    {
        var total = aiSucceeded + aiFailed;
        var rate = total == 0 ? 0d : (double)aiFailed / total;
        var evidence = $"近 24 小時 AI 呼叫：{total} 次，失敗 {aiFailed} 次"
            + (total == 0 ? string.Empty : $"（{rate:P0}）")
            + $"，使用者取消不算失敗；今日日誌 ERROR／FATAL：{logErrorCount} 筆。";

        if (total >= MinimumCallsForFailureRate && rate > 0.5)
        {
            return new(SystemHealthStatus.Unhealthy, evidence, "AI 呼叫有一半以上失敗，請到「AI 用量分析」看失敗原因。");
        }

        var failedTooOften = total >= MinimumCallsForFailureRate ? rate > 0.2 : aiFailed > 0;
        if (failedTooOften || logErrorCount > 0)
        {
            var reasons = new List<string>();
            if (failedTooOften)
            {
                reasons.Add("AI 呼叫失敗偏多，可到「AI 用量分析」看失敗原因");
            }

            if (logErrorCount > 0)
            {
                reasons.Add("今日日誌有錯誤紀錄，請看下方日誌");
            }

            return new(SystemHealthStatus.Degraded, evidence, string.Join("；", reasons) + "。");
        }

        return new(SystemHealthStatus.Healthy, evidence);
    }

    private static bool HasTextPricing(LlmSettings settings, string model)
        => settings.Pricing.TryGetValue(model.Trim(), out var price)
            && price.InputPerMillionTokens is not null
            && price.OutputPerMillionTokens is not null;

    private static bool HasAudioPricing(LlmSettings settings, string model)
        => settings.Pricing.TryGetValue(model.Trim(), out var price)
            && price.AudioPerMinute is not null;

    private static string DescribeSource(ExchangeRateSource source) => source switch
    {
        ExchangeRateSource.Live => "即時抓取",
        ExchangeRateSource.Ledger => "用量帳本最後一筆舊匯率",
        _ => "設定檔保底值",
    };
}
