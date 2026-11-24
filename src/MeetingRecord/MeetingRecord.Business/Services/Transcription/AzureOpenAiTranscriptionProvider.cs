using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;
using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// Azure OpenAI 的語音轉錄實作（audio/transcriptions 端點）。
///
/// 連線資訊一律取自 <see cref="LlmSettings"/>（禁止直接讀 IConfiguration）；
/// 部署名稱取 <see cref="LlmProviderSettings.TranscriptionModel"/>，例如 gpt-4o-transcribe。
/// 部署名稱含 <c>diarize</c> 時改要講者（見 <see cref="IsDiarizeModel"/>）；1.1.64 起可帶聲音參考讓整場代號一致。
/// </summary>
public class AzureOpenAiTranscriptionProvider : ISpeakerAwareTranscriptionProvider
{
    /// <summary>具名 HttpClient 名稱，逾時在 DI 註冊處拉長（轉錄為長時間請求）。</summary>
    public const string HttpClientName = "AzureOpenAiTranscription";

    public const string AzureOpenAiProviderName = "AzureOpenAI";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<AzureOpenAiTranscriptionProvider> logger;
    private readonly TranscriptionOverrides? overrides;

    public AzureOpenAiTranscriptionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LlmSettings> llmSettings,
        ILogger<AzureOpenAiTranscriptionProvider> logger)
        : this(httpClientFactory, llmSettings, logger, overrides: null)
    {
    }

    private AzureOpenAiTranscriptionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LlmSettings> llmSettings,
        ILogger<AzureOpenAiTranscriptionProvider> logger,
        TranscriptionOverrides? overrides)
    {
        this.httpClientFactory = httpClientFactory;
        this.llmSettings = llmSettings;
        this.logger = logger;
        this.overrides = overrides;
    }

    /// <summary>
    /// 1.1.72：轉錄比較工具專用——換部署、加語言或提示詞，連線與金鑰照設定。**正式轉錄不用它**，仍只讀設定。
    /// 回傳新的實例，<see cref="ModelName"/> 也跟著換，帳本才會記在實際打的部署上、用它的單價。
    /// </summary>
    public AzureOpenAiTranscriptionProvider WithOverrides(TranscriptionOverrides overrides)
        => new(httpClientFactory, llmSettings, logger, overrides);

    public string ProviderName => AzureOpenAiProviderName;

    /// <summary>目前生效的轉錄 deployment 名稱。用量帳本以它查單價。</summary>
    public string ModelName =>
        NullIfBlank(overrides?.Deployment) ?? llmSettings.Value.GetTranscriptionProvider()?.TranscriptionModel ?? string.Empty;

    /// <summary>1.1.64：部署會分講者時，才能帶聲音參考讓整場代號一致。</summary>
    public bool SupportsSpeakers => IsDiarizeModel(ModelName);

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
    {
        var (body, isDiarize) = await SendAsync(audio, fileName, references: [], cancellationToken);
        return isDiarize ? DiarizedTranscriptFormatter.Format(body) : body.Trim();
    }

    public async Task<DiarizedTranscription> TranscribeWithSpeakersAsync(
        Stream audio,
        string fileName,
        IReadOnlyList<SpeakerReference> references,
        CancellationToken cancellationToken)
    {
        var (body, isDiarize) = await SendAsync(audio, fileName, references, cancellationToken);
        return isDiarize
            ? DiarizedTranscriptFormatter.Parse(body)
            : new DiarizedTranscription([], body.Trim());
    }

    private async Task<(string Body, bool IsDiarize)> SendAsync(
        Stream audio,
        string fileName,
        IReadOnlyList<SpeakerReference> references,
        CancellationToken cancellationToken)
    {
        var settings = llmSettings.Value;
        var provider = settings.GetTranscriptionProvider()
            ?? throw new InvalidOperationException(
                $"尚未設定語音轉錄供應商，請在 {LlmSettings.SectionName}:TranscriptionProvider 或 {LlmSettings.SectionName}:DefaultProvider 指定。");

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException(
                $"{LlmSettings.SectionName}:Providers:{settings.EffectiveTranscriptionProviderName}:ApiKey 未設定，無法呼叫語音轉錄 API。");
        }

        var modelKey = AzureDeploymentErrors.ProviderKey(settings.EffectiveTranscriptionProviderName, nameof(LlmProviderSettings.TranscriptionModel));
        var deployment = NullIfBlank(overrides?.Deployment) ?? provider.TranscriptionModel;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(AzureDeploymentErrors.MissingDeployment("語音轉錄", modelKey));
        }

        var requestUri = BuildRequestUri(provider.Endpoint, deployment, provider.EffectiveTranscriptionApiVersion);

        using var content = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(audioContent, "file", fileName);

        // 1.1.63：diarize 模型要 diarized_json 才拿得到講者；輸入超過 30 秒時 chunking_strategy 是必填，不帶直接回 400。
        var isDiarize = IsDiarizeModel(deployment);
        content.Add(new StringContent(isDiarize ? "diarized_json" : "text"), "response_format");

        // 1.1.72：只有比較工具會帶。diarize 模型不支援 prompt，一律不送（送了直接 400）。
        if (NullIfBlank(overrides?.Language) is { } language)
        {
            content.Add(new StringContent(language), "language");
        }

        if (!isDiarize && NullIfBlank(overrides?.Prompt) is { } prompt)
        {
            content.Add(new StringContent(prompt), "prompt");
        }
        if (isDiarize)
        {
            content.Add(new StringContent("auto"), "chunking_strategy");

            // 1.1.64：已知講者的聲音參考（最多 4 份），認得的講者回傳參考的名稱，整場代號才會一致。
            foreach (var reference in references)
            {
                content.Add(new StringContent(reference.Name), "known_speaker_names[]");
                content.Add(new StringContent(reference.DataUrl), "known_speaker_references[]");
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        request.Headers.Add("api-key", provider.ApiKey);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        logger.LogDebug(
            "Sending Azure OpenAI transcription request. FileName={FileName}, SpeakerReferences={SpeakerReferences}",
            fileName,
            references.Count);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure OpenAI transcription request failed. StatusCode={StatusCode}, FileName={FileName}",
                (int)response.StatusCode,
                fileName);

            throw new AiProviderHttpException(
                (int)response.StatusCode,
                AzureDeploymentErrors.Describe(
                    "語音轉錄", (int)response.StatusCode, response.ReasonPhrase, body, deployment, provider.Endpoint, modelKey));
        }

        return (body, isDiarize);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 部署是不是會分講者的 diarize 模型。Azure 的部署名稱是自訂的，這裡以名稱含 <c>diarize</c> 判斷，
    /// 部署時名稱要保留這個字（例如 <c>gpt-4o-transcribe-diarize</c>）。
    /// </summary>
    internal static bool IsDiarizeModel(string? deploymentName) =>
        deploymentName?.Contains("diarize", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 組出 Azure OpenAI 的轉錄端點位址。抽成純函式以便單元測試。
    /// ⚠️ 1.0.28 文字生成改走 v1，轉錄**刻意不改**：v1 的 <c>/openai/v1/audio/transcriptions</c>
    /// 打 gpt-transcribe 部署一律 404 DeploymentNotFound（換主機、加 api-version=preview、調換欄位順序都一樣），
    /// 舊路徑則正常。api-version 用 <see cref="LlmProviderSettings.EffectiveTranscriptionApiVersion"/>（未設定時用內建值）。
    /// </summary>
    internal static Uri BuildRequestUri(string endpoint, string deploymentName, string apiVersion)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 Endpoint 不可為空白。");
        }

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 TranscriptionModel 不可為空白。");
        }

        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 TranscriptionApiVersion 不可為空白。");
        }

        var baseAddress = endpoint.Trim().TrimEnd('/');
        var deployment = Uri.EscapeDataString(deploymentName.Trim());
        var version = Uri.EscapeDataString(apiVersion.Trim());

        return new Uri($"{baseAddress}/openai/deployments/{deployment}/audio/transcriptions?api-version={version}");
    }
}

/// <summary>
/// 1.1.72 轉錄比較用的覆寫：空白＝沿用設定（部署）或不送（語言、提示詞）。
/// </summary>
/// <param name="Deployment">Azure 部署名稱；名稱含 diarize 時走分講者格式。</param>
/// <param name="Language">ISO-639-1，例如 <c>zh</c>。</param>
/// <param name="Prompt">專有名詞提示；diarize 模型不支援，不會送出。</param>
public sealed record TranscriptionOverrides(string? Deployment, string? Language, string? Prompt);
