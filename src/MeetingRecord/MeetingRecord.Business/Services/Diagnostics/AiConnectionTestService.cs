using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Diagnostics;

/// <summary>一個探測的結果（1.0.35）。</summary>
/// <param name="Item">「文字生成」或「語音轉錄」。</param>
/// <param name="Success">部署存在而且呼叫成功。</param>
/// <param name="Deployment">實際送出的部署名稱；未設定時為空字串。</param>
/// <param name="Host">端點主機；未設定時為空字串。</param>
/// <param name="Elapsed">從送出到收到回應的時間；沒有送出時為 null。</param>
/// <param name="Message">給管理者看的結論或失敗原因。</param>
public sealed record AiConnectionProbeResult(
    string Item,
    bool Success,
    string Deployment,
    string Host,
    TimeSpan? Elapsed,
    string Message);

/// <summary>
/// 「測試 AI 連線」（1.0.35，系統健康監控頁，管理者手動按）。實際呼叫一次文字生成與語音轉錄，驗證部署存在、金鑰可用。
///
/// <para>為什麼需要：設定只看得出「填了什麼」，看不出 Azure 上有沒有這個部署。1.0.34 部署到 IIS 時把鍵寫成
/// <c>Deployment</c>（當時還不是別名），系統悄悄用了預設的 <c>gpt-4o-mini</c>，直到產生會議紀錄才 404。</para>
///
/// <para>⚠️ <b>會產生費用</b>（極少：一則極短提問、1 秒靜音）：一律經 <see cref="AiCallGateway"/>，
/// 記入用量帳本（功能別「AI 連線測試」）與 AI 對話紀錄，也受每月用量上限管制。
/// 呼叫端（畫面）必須先跳「（會產生費用）」確認窗；健康監控每次開頁的自動檢查<b>絕不</b>呼叫這裡。</para>
/// </summary>
public class AiConnectionTestService
{
    public const string TextItem = "文字生成";
    public const string TranscriptionItem = "語音轉錄";

    /// <summary>每個探測最多等多久。真的連得上時通常幾秒內就回來。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private const string SystemPrompt = "你是連線測試。";
    private const string UserPrompt = "請只回答 OK。";

    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly IEnumerable<ITranscriptionProvider> transcriptionProviders;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly AiCallGateway gateway;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<AiConnectionTestService> logger;

    public AiConnectionTestService(
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        IEnumerable<ITranscriptionProvider> transcriptionProviders,
        IOptions<LlmSettings> llmSettings,
        AiCallGateway gateway,
        CurrentUserService currentUserService,
        ILogger<AiConnectionTestService> logger)
    {
        this.textGenerationProviders = textGenerationProviders;
        this.transcriptionProviders = transcriptionProviders;
        this.llmSettings = llmSettings;
        this.gateway = gateway;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>依序測文字生成與語音轉錄。任何一個失敗都不會丟例外，結果寫在回傳值裡。</summary>
    public async Task<IReadOnlyList<AiConnectionProbeResult>> RunAsync(CancellationToken cancellationToken = default)
        => [await TestTextGenerationAsync(cancellationToken), await TestTranscriptionAsync(cancellationToken)];

    public async Task<AiConnectionProbeResult> TestTextGenerationAsync(CancellationToken cancellationToken = default)
    {
        var settings = llmSettings.Value;
        var providerSettings = TryGet(settings.GetDefaultProvider);
        var deployment = providerSettings?.Model.Trim() ?? string.Empty;
        var host = HostOf(providerSettings?.Endpoint);

        if (providerSettings is null)
        {
            return NotConfigured(TextItem, $"尚未設定文字生成供應商（{LlmSettings.SectionName}:DefaultProvider）。");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            return NotConfigured(TextItem, AzureDeploymentErrors.MissingDeployment(TextItem,
                AzureDeploymentErrors.ProviderKey(settings.DefaultProvider, nameof(LlmProviderSettings.Model))), host);
        }

        var provider = textGenerationProviders.FirstOrDefault(x =>
            string.Equals(x.ProviderName, settings.DefaultProvider.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return NotConfigured(TextItem, $"找不到名為「{settings.DefaultProvider.Trim()}」的文字生成供應商實作。", host, deployment);
        }

        return await ProbeAsync(TextItem, deployment, host, cancellationToken, async token =>
        {
            var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
            var result = await gateway.GenerateAsync(
                provider,
                new AiCallContext(AiUsageFeature.ConnectionTest, userId, userName, RelatedInfo: "AI 連線測試：文字生成"),
                SystemPrompt,
                UserPrompt,
                onDelta: null,
                token);
            return $"連線成功，部署有回應（{result.Content.Length} 字）。";
        });
    }

    public async Task<AiConnectionProbeResult> TestTranscriptionAsync(CancellationToken cancellationToken = default)
    {
        var settings = llmSettings.Value;
        if (string.IsNullOrWhiteSpace(settings.EffectiveTranscriptionProviderName))
        {
            return NotConfigured(TranscriptionItem, "未啟用語音轉錄（沒有指定供應商）。");
        }

        settings.Providers.TryGetValue(settings.EffectiveTranscriptionProviderName, out var providerSettings);
        var deployment = providerSettings?.TranscriptionModel.Trim() ?? string.Empty;
        var host = HostOf(providerSettings?.Endpoint);

        if (providerSettings is null)
        {
            return NotConfigured(TranscriptionItem, $"{LlmSettings.SectionName}:Providers 裡找不到「{settings.EffectiveTranscriptionProviderName}」。");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            return NotConfigured(TranscriptionItem, AzureDeploymentErrors.MissingDeployment(TranscriptionItem,
                AzureDeploymentErrors.ProviderKey(settings.EffectiveTranscriptionProviderName, nameof(LlmProviderSettings.TranscriptionModel))), host);
        }

        var provider = transcriptionProviders.FirstOrDefault(x =>
            string.Equals(x.ProviderName, settings.EffectiveTranscriptionProviderName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return NotConfigured(TranscriptionItem, $"找不到名為「{settings.EffectiveTranscriptionProviderName}」的語音轉錄供應商實作。", host, deployment);
        }

        return await ProbeAsync(TranscriptionItem, deployment, host, cancellationToken, async token =>
        {
            var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
            using var audio = new MemoryStream(BuildSilentWav(TimeSpan.FromSeconds(1)));
            await gateway.TranscribeAsync(
                provider,
                new AiCallContext(AiUsageFeature.TranscriptionConnectionTest, userId, userName, RelatedInfo: "AI 連線測試：語音轉錄"),
                audio,
                "connection-test.wav",
                audioSeconds: 1,
                isDurationEstimated: false,
                segmentIndex: 1,
                segmentCount: 1,
                token);
            return "連線成功，部署有回應（送出 1 秒靜音）。";
        });
    }

    private async Task<AiConnectionProbeResult> ProbeAsync(
        string item,
        string deployment,
        string host,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string>> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var message = await call(timeout.Token);
            return new AiConnectionProbeResult(item, true, deployment, host, stopwatch.Elapsed, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"等了 {ProbeTimeout.TotalSeconds:0} 秒沒有回應（連線逾時）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI connection test failed. Item={Item}, Deployment={Deployment}, Host={Host}", item, deployment, host);
            return Failed(Classify(ex, deployment));
        }

        AiConnectionProbeResult Failed(string reason)
            => new(item, false, deployment, host, stopwatch.Elapsed, reason);
    }

    /// <summary>把例外翻成管理者看得懂的原因。抽成 internal static 以便單元測試。</summary>
    internal static string Classify(Exception exception, string deployment) => exception switch
    {
        AiProviderHttpException { StatusCode: 404 } http when http.Message.Contains("找不到 Azure 部署", StringComparison.Ordinal)
            => $"部署「{deployment}」在 Azure 上不存在。{http.Message}",
        AiProviderHttpException { StatusCode: 401 or 403 } http
            => $"金鑰錯誤或沒有權限（HTTP {http.StatusCode}）。{http.Message}",
        AiQuotaExceededException quota => $"已達 AI 用量上限，沒有送出。{quota.Message}",
        _ => Truncate(exception.Message, 300),
    };

    /// <summary>
    /// 產生一段 16kHz、16 位元、單聲道的靜音 WAV。轉錄探測用：不需要任何檔案，金額以秒計、極小。
    /// 抽成 internal static 以便單元測試。
    /// </summary>
    internal static byte[] BuildSilentWav(TimeSpan duration)
    {
        const int sampleRate = 16_000;
        const short bitsPerSample = 16;
        const short channels = 1;
        var dataLength = (int)(sampleRate * duration.TotalSeconds) * channels * bitsPerSample / 8;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }

    private static AiConnectionProbeResult NotConfigured(string item, string message, string host = "", string deployment = "")
        => new(item, false, deployment, host, null, message);

    private static LlmProviderSettings? TryGet(Func<LlmProviderSettings?> get)
    {
        try
        {
            return get();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string HostOf(string? endpoint)
        => !string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
