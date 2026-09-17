using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// Azure OpenAI 的 chat completions 實作。
///
/// 形狀刻意比照 <c>AzureOpenAiTranscriptionProvider</c>：具名 HttpClient、api-key 標頭、
/// 位址組裝抽成可測試的純函式、不做重試（失敗直接往上拋給 job runner 記錄）。
/// 唯一的差異是這裡收送 JSON，轉錄端收送的是 multipart 與純文字。
///
/// <para>
/// 0.4.42 起改用串流（SSE）。動機是進度：非串流的單次呼叫在整段生成期間沒有任何訊號，
/// 畫面只能停在固定的 70% 直到完成。串流讓「已產生多少字」成為真實可回報的進度來源。
/// </para>
/// </summary>
public class AzureOpenAiTextGenerationProvider : ITextGenerationProvider
{
    /// <summary>具名 HttpClient 名稱，逾時在 DI 註冊處拉長（長逐字稿的生成可能耗時數分鐘）。</summary>
    public const string HttpClientName = "AzureOpenAiTextGeneration";

    public const string AzureOpenAiProviderName = "AzureOpenAI";

    /// <summary>SSE 的資料行前綴。規格允許冒號後面沒有空白，所以只押到冒號。</summary>
    private const string DataPrefix = "data:";

    /// <summary>SSE 串流結束的哨兵值。</summary>
    private const string DoneSentinel = "[DONE]";

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

    /// <summary>目前生效的 deployment 名稱。用量帳本以它查單價。</summary>
    public string ModelName => llmSettings.Value.GetDefaultProvider()?.Model ?? string.Empty;

    public async Task<TextGenerationResult> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onDelta,
        CancellationToken cancellationToken)
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
            Stream = true,

            // ⚠️ 串流預設**不回傳** usage，必須明確要求（0.4.80 起，用量帳本靠它）。
            // 需要 api-version 2024-10-21 以上；舊版本會回 HTTP 400
            // 「Unrecognized request argument supplied: stream_options」而不是忽略它——
            // 也就是說把 ApiVersion 往回調會讓全站生成掛掉，而錯誤訊息看起來像金鑰壞了。
            StreamOptions = new StreamOptions { IncludeUsage = true },

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

        // ResponseHeadersRead 是串流的前提——預設會等整個 body 收完才回來，那就沒有「邊收邊回報」可言。
        // 代價是 HttpClient.Timeout 只涵蓋到收到 response headers 為止，讀取串流的階段不再受逾時保護；
        // 掛住的連線只能靠 cancellationToken 收掉（背景服務停止時會取消）。
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 失敗時回的是一般 JSON 錯誤內容而不是 SSE，照舊整份讀出來當訊息。
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "Azure OpenAI chat completion request failed. StatusCode={StatusCode}",
                (int)response.StatusCode);

            throw new InvalidOperationException(
                $"文字生成 API 回應 {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(errorBody, 500)}");
        }

        var builder = new StringBuilder();
        AiTokenUsage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                // 空行（事件分隔）與註解行都會走到這裡。
                continue;
            }

            var data = line[DataPrefix.Length..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (string.Equals(data, DoneSentinel, StringComparison.Ordinal))
            {
                break;
            }

            // 一個片段只反序列化一次，delta 與 usage 都從這裡取。
            var chunk = ParseChunk(data);

            // ⚠️ usage 必須在 delta 的空值檢查**之前**取。
            // 帶 usage 的那一片 choices 是**空陣列**，所以它取不到 delta；
            // 放在下面的 continue 之後就會被整片吃掉——0.4.80 之前的 bug 正是長這樣。
            if (ExtractUsage(chunk) is { } chunkUsage)
            {
                usage = chunkUsage;
            }

            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            builder.Append(delta);
            onDelta?.Invoke(delta);
        }

        if (builder.Length == 0)
        {
            // 內容過濾或模型無輸出時會走到這裡，必須是失敗而不是靜靜寫入空草稿。
            throw new InvalidOperationException("文字生成 API 未回傳任何內容。");
        }

        // usage 為 null 是可接受的結果（伺服器沒送 [DONE] 就關連線、或 api-version 不支援）。
        // ⚠️ 絕不能因此拋例外——那會把「生成成功但拿不到帳」升級成「生成失敗」。
        return new TextGenerationResult(builder.ToString().Trim(), usage);
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

    /// <summary>
    /// 從單一 SSE 資料片段取出增量文字；這一片沒有文字時回傳 null。
    ///
    /// <para>
    /// 沒有文字是常態而非異常：Azure 的第一片通常只帶內容過濾註記、
    /// 最後一片只帶 finish_reason，兩者都不含 delta 內容。
    /// </para>
    ///
    /// 抽成純函式以便單元測試。
    /// </summary>
    internal static string? ExtractDelta(string dataPayload)
        => ParseChunk(dataPayload)?.Choices?.FirstOrDefault()?.Delta?.Content;

    /// <summary>
    /// 反序列化單一 SSE 資料片段。抽出來是為了讓一個片段**只解析一次**，
    /// delta 與 usage 都從同一個結果取。
    /// </summary>
    private static ChatCompletionChunk? ParseChunk(string dataPayload)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatCompletionChunk>(dataPayload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"文字生成 API 的串流片段不是預期的 JSON 格式：{Truncate(dataPayload, 500)}", ex);
        }
    }

    /// <summary>
    /// 從單一片段取出 token 用量；這一片沒有用量時回 null。
    ///
    /// <para>
    /// ⚠️ 開了 <c>include_usage</c> 之後，**中間每一片都會明確帶 <c>"usage": null</c>**，
    /// 所以一定要做 null 檢查，不能用「有 usage 這個欄位就是最後一片」來判斷。
    /// </para>
    /// </summary>
    internal static AiTokenUsage? ExtractUsage(string dataPayload) => ExtractUsage(ParseChunk(dataPayload));

    private static AiTokenUsage? ExtractUsage(ChatCompletionChunk? chunk)
    {
        if (chunk?.Usage is not { } usage)
        {
            return null;
        }

        return new AiTokenUsage(
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.PromptTokensDetails?.CachedTokens);
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

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        /// <summary>
        /// 留 null 時不會被送出（SerializerOptions 是 WhenWritingNull），
        /// 所以不支援這個參數的供應商可以安全地不設定它。
        /// </summary>
        [JsonPropertyName("stream_options")]
        public StreamOptions? StreamOptions { get; set; }
    }

    private sealed class StreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionChunk
    {
        [JsonPropertyName("choices")]
        public List<ChatChunkChoice>? Choices { get; set; }

        /// <summary>
        /// ⚠️ 只有**最後一片**會帶（且該片的 <c>choices</c> 是空陣列）；
        /// 中間每一片都是明確的 null。它在 <c>finish_reason</c> 那片之後、<c>[DONE]</c> 之前到達。
        /// </summary>
        [JsonPropertyName("usage")]
        public ChatUsage? Usage { get; set; }
    }

    private sealed class ChatUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("prompt_tokens_details")]
        public ChatPromptTokensDetails? PromptTokensDetails { get; set; }
    }

    private sealed class ChatPromptTokensDetails
    {
        /// <summary>命中提示詞快取的輸入 token。⚠️ 已含在 <c>prompt_tokens</c> 裡，不可再加一次。</summary>
        [JsonPropertyName("cached_tokens")]
        public int? CachedTokens { get; set; }
    }

    private sealed class ChatChunkChoice
    {
        [JsonPropertyName("delta")]
        public ChatDelta? Delta { get; set; }
    }

    private sealed class ChatDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    #endregion
}
