using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// Azure OpenAI 的語音轉錄實作（audio/transcriptions 端點）。
///
/// 連線資訊一律取自 <see cref="LlmSettings"/>（禁止直接讀 IConfiguration）；
/// 部署名稱取 <see cref="LlmProviderSettings.TranscriptionModel"/>，例如 gpt-4o-transcribe。
/// </summary>
public class AzureOpenAiTranscriptionProvider : ITranscriptionProvider
{
    /// <summary>具名 HttpClient 名稱，逾時在 DI 註冊處拉長（轉錄為長時間請求）。</summary>
    public const string HttpClientName = "AzureOpenAiTranscription";

    public const string AzureOpenAiProviderName = "AzureOpenAI";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<AzureOpenAiTranscriptionProvider> logger;

    public AzureOpenAiTranscriptionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LlmSettings> llmSettings,
        ILogger<AzureOpenAiTranscriptionProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.llmSettings = llmSettings;
        this.logger = logger;
    }

    public string ProviderName => AzureOpenAiProviderName;

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
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

        var requestUri = BuildRequestUri(provider.Endpoint, provider.TranscriptionModel, provider.EffectiveTranscriptionApiVersion);

        using var content = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(audioContent, "file", fileName);
        content.Add(new StringContent("text"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        request.Headers.Add("api-key", provider.ApiKey);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        logger.LogDebug("Sending Azure OpenAI transcription request. FileName={FileName}", fileName);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure OpenAI transcription request failed. StatusCode={StatusCode}, FileName={FileName}",
                (int)response.StatusCode,
                fileName);

            throw new InvalidOperationException(
                $"語音轉錄 API 回應 {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(body, 500)}");
        }

        return body.Trim();
    }

    /// <summary>
    /// 組出 Azure OpenAI 的轉錄端點位址。抽成純函式以便單元測試。
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
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 ApiVersion／TranscriptionApiVersion 不可為空白。");
        }

        var baseAddress = endpoint.Trim().TrimEnd('/');
        var deployment = Uri.EscapeDataString(deploymentName.Trim());
        var version = Uri.EscapeDataString(apiVersion.Trim());

        return new Uri($"{baseAddress}/openai/deployments/{deployment}/audio/transcriptions?api-version={version}");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength), "…");
    }
}
