using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// Azure OpenAI 的 chat completions 實作。
///
/// 形狀刻意比照 <c>AzureOpenAiTranscriptionProvider</c>：具名 HttpClient、api-key 標頭、
/// 位址組裝抽成可測試的純函式、不做重試（失敗直接往上拋給 job runner 記錄）。
/// 唯一的差異是這裡收送 JSON，轉錄端收送的是 multipart 與純文字。
/// </summary>
public class AzureOpenAiTextGenerationProvider : ITextGenerationProvider
{
    /// <summary>具名 HttpClient 名稱，逾時在 DI 註冊處拉長（長逐字稿的生成可能耗時數分鐘）。</summary>
    public const string HttpClientName = "AzureOpenAiTextGeneration";

    public const string AzureOpenAiProviderName = "AzureOpenAI";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<AzureOpenAiTextGenerationProvider> logger;

    public AzureOpenAiTextGenerationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LlmSettings> llmSettings,
        ILogger<AzureOpenAiTextGenerationProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.llmSettings = llmSettings;
        this.logger = logger;
    }

    public string ProviderName => AzureOpenAiProviderName;

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var settings = llmSettings.Value;
        var provider = settings.GetDefaultProvider()
            ?? throw new InvalidOperationException(
                $"尚未設定文字生成供應商，請在 {LlmSettings.SectionName}:DefaultProvider 指定。");

        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException(
                $"{LlmSettings.SectionName}:Providers:{settings.DefaultProvider.Trim()}:ApiKey 未設定，無法呼叫文字生成 API。");
        }

        var requestUri = BuildRequestUri(provider.Endpoint, provider.Model, provider.ApiVersion);

        var payload = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            ],
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("api-key", provider.ApiKey);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        logger.LogDebug("Sending Azure OpenAI chat completion request. PromptLength={PromptLength}", userPrompt.Length);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure OpenAI chat completion request failed. StatusCode={StatusCode}",
                (int)response.StatusCode);

            throw new InvalidOperationException(
                $"文字生成 API 回應 {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(body, 500)}");
        }

        return ExtractContent(body);
    }

    /// <summary>組出 Azure OpenAI 的 chat completions 端點位址。抽成純函式以便單元測試。</summary>
    internal static Uri BuildRequestUri(string endpoint, string deploymentName, string apiVersion)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 Endpoint 不可為空白。");
        }

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 Model 不可為空白。");
        }

        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            throw new InvalidOperationException($"{LlmSettings.SectionName} 的 ApiVersion 不可為空白。");
        }

        var baseAddress = endpoint.Trim().TrimEnd('/');
        var deployment = Uri.EscapeDataString(deploymentName.Trim());
        var version = Uri.EscapeDataString(apiVersion.Trim());

        return new Uri($"{baseAddress}/openai/deployments/{deployment}/chat/completions?api-version={version}");
    }

    /// <summary>從回應 JSON 取出第一個 choice 的訊息內容。抽成純函式以便單元測試。</summary>
    internal static string ExtractContent(string responseBody)
    {
        ChatCompletionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"文字生成 API 回應不是預期的 JSON 格式：{Truncate(responseBody, 500)}", ex);
        }

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"文字生成 API 未回傳任何內容：{Truncate(responseBody, 500)}");
        }

        return content.Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength), "…");
    }

    #region 請求／回應的 JSON 形狀（僅取用到的欄位）

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    #endregion
}
