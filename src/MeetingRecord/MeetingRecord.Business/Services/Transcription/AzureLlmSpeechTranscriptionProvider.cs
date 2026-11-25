using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 1.1.73：Azure AI Speech 的 LLM Speech（Fast Transcription API 開 <c>enhancedMode</c>）。
///
/// <para>
/// 和 Azure OpenAI 的差別：一次送整個檔案（上限見 <see cref="AzureSpeechSettings.MaxFileSeconds"/>），
/// 分講者時講者編號在這一次呼叫內一致、最多 35 位，不需要聲音參考（<c>references</c> 一律忽略）。
/// 每句都有時間，<see cref="TranscriptionJobRunner"/> 照時間剪試聽片段。
/// </para>
///
/// <para>
/// 不註冊成 <see cref="ITranscriptionProvider"/>：引擎是每場會議自己選的（<c>Meeting.TranscriptionEngine</c>），
/// 不走 <c>LlmSettings.TranscriptionProvider</c> 的全域切換。
/// </para>
/// </summary>
public class AzureLlmSpeechTranscriptionProvider : ISpeakerAwareTranscriptionProvider
{
    /// <summary>具名 HttpClient 名稱，逾時依 <see cref="AzureSpeechSettings.TimeoutMinutes"/>。</summary>
    public const string HttpClientName = "AzureLlmSpeechTranscription";

    public const string LlmSpeechProviderName = "AzureLlmSpeech";

    /// <summary>片語清單的上限：太多會拖慢辨識，而人名＋專案名稱遠用不到。</summary>
    public const int MaxPhrases = 500;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<TranscriptionSettings> settings;
    private readonly ILogger<AzureLlmSpeechTranscriptionProvider> logger;
    private readonly LlmSpeechOptions options;

    public AzureLlmSpeechTranscriptionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<TranscriptionSettings> settings,
        ILogger<AzureLlmSpeechTranscriptionProvider> logger)
        : this(httpClientFactory, settings, logger, new LlmSpeechOptions(Diarize: true, MaxSpeakers: null, Phrases: []))
    {
    }

    private AzureLlmSpeechTranscriptionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<TranscriptionSettings> settings,
        ILogger<AzureLlmSpeechTranscriptionProvider> logger,
        LlmSpeechOptions options)
    {
        this.httpClientFactory = httpClientFactory;
        this.settings = settings;
        this.logger = logger;
        this.options = options;
    }

    /// <summary>這一場要不要分講者、講者上限、片語清單。回傳新的實例。</summary>
    public AzureLlmSpeechTranscriptionProvider WithOptions(LlmSpeechOptions options)
        => new(httpClientFactory, settings, logger, options);

    public string ProviderName => LlmSpeechProviderName;

    /// <summary>帳本與單價表的鍵（LLM Speech 沒有部署名稱）。</summary>
    public string ModelName => AzureSpeechSettings.PricingModel;

    public bool SupportsSpeakers => options.Diarize;

    public bool IsConfigured => settings.Value.AzureSpeech.IsConfigured;

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
    {
        var result = await TranscribeWithSpeakersAsync(audio, fileName, [], cancellationToken);
        return FormatTranscript(result.Segments, result.FallbackText, options.Diarize);
    }

    public async Task<DiarizedTranscription> TranscribeWithSpeakersAsync(
        Stream audio,
        string fileName,
        IReadOnlyList<SpeakerReference> references,
        CancellationToken cancellationToken)
    {
        var speech = settings.Value.AzureSpeech;
        if (!speech.IsConfigured)
        {
            throw new InvalidOperationException(
                $"尚未設定 Azure LLM Speech，請在 {TranscriptionSettings.SectionName}:AzureSpeech 填寫 Endpoint 與 ApiKey。");
        }

        var requestUri = BuildRequestUri(speech.Endpoint, speech.ApiVersion);

        using var content = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Add(audioContent, "audio", fileName);
        content.Add(
            new StringContent(BuildDefinition(speech.Locale, options), Encoding.UTF8, "application/json"),
            "definition");

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        request.Headers.Add("Ocp-Apim-Subscription-Key", speech.ApiKey);

        logger.LogDebug(
            "Sending Azure LLM Speech transcription request. FileName={FileName}, Diarize={Diarize}, MaxSpeakers={MaxSpeakers}, Phrases={Phrases}",
            fileName,
            options.Diarize,
            options.MaxSpeakers,
            options.Phrases.Count);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure LLM Speech transcription request failed. StatusCode={StatusCode}, FileName={FileName}",
                (int)response.StatusCode,
                fileName);

            throw new AiProviderHttpException((int)response.StatusCode, DescribeFailure((int)response.StatusCode, body));
        }

        return Parse(body, options.Diarize);
    }

    /// <summary>
    /// 組出 definition JSON。抽成純函式以便單元測試。
    /// 分講者時 maxSpeakers 夾在 2～35；片語去空白、去重複、最多 <see cref="MaxPhrases"/> 個。
    /// </summary>
    internal static string BuildDefinition(string? locale, LlmSpeechOptions options)
    {
        var definition = new Dictionary<string, object>
        {
            ["enhancedMode"] = new Dictionary<string, object> { ["enabled"] = true, ["task"] = "transcribe" },
        };

        if (!string.IsNullOrWhiteSpace(locale))
        {
            definition["locales"] = new[] { locale.Trim() };
        }

        if (options.Diarize)
        {
            definition["diarization"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["maxSpeakers"] = Math.Clamp(options.MaxSpeakers ?? 10, AzureSpeechSettings.MinSpeakers, AzureSpeechSettings.MaxSpeakers),
            };
        }

        var phrases = options.Phrases
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPhrases)
            .ToList();
        if (phrases.Count > 0)
        {
            definition["phraseList"] = new Dictionary<string, object> { ["phrases"] = phrases };
        }

        return JsonSerializer.Serialize(definition);
    }

    /// <summary>
    /// 解析回應：每個 phrase 一句，時間換成秒；分講者時 speaker 是整數，轉成「1」「2」…。
    /// 沒有 phrases 時退回 combinedPhrases 的文字。不分講者時 segments 一律空、只回文字。
    /// </summary>
    internal static DiarizedTranscription Parse(string? body, bool diarize)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new DiarizedTranscription([], string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DiarizedTranscription([], string.Empty);
            }

            var combined = root.TryGetProperty("combinedPhrases", out var combinedItems) && combinedItems.ValueKind == JsonValueKind.Array
                ? string.Join("\n", combinedItems.EnumerateArray()
                    .Select(x => ReadString(x, "text"))
                    .Where(x => !string.IsNullOrEmpty(x)))
                : string.Empty;

            var segments = new List<DiarizedSegment>();
            if (root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Array)
            {
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var text = ReadString(phrase, "text");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var speaker = phrase.TryGetProperty("speaker", out var speakerValue) && speakerValue.ValueKind == JsonValueKind.Number
                        ? speakerValue.GetInt32().ToString(CultureInfo.InvariantCulture)
                        : TranscriptSpeakerLabels.UnknownSpeaker;
                    var start = ReadMilliseconds(phrase, "offsetMilliseconds");
                    var duration = ReadMilliseconds(phrase, "durationMilliseconds");
                    segments.Add(new DiarizedSegment(speaker, text, start, start + duration));
                }
            }

            // 不分講者：沒有講者代號，直接回分好段落的文字（segments 留空，呼叫端就不會加「講者」）。
            return diarize
                ? new DiarizedTranscription(segments, combined)
                : new DiarizedTranscription([], segments.Count > 0 ? FormatTranscript(segments, combined, diarize: false) : combined);
        }
        catch (JsonException)
        {
            return new DiarizedTranscription([], body.Trim());
        }
    }

    /// <summary>
    /// 每 <paramref name="paragraphSeconds"/> 秒（依每句的開始時間）分一個段落，段落之間用 <c>\n\n</c>。
    ///
    /// <para>
    /// ⚠️ 一定要分：LLM Speech 一次回整場，而 <c>\n\n</c> 是逐字稿的分段邊界——產生會議紀錄的
    /// <c>TranscriptChunker</c> 與 <see cref="TranscriptionNoiseFilter"/> 都靠它切。整場一大塊時只能在句子中間硬切。
    /// 5 分鐘和 Azure OpenAI 每段的長度一樣，兩種引擎的逐字稿形狀相同。
    /// </para>
    /// </summary>
    public static string FormatTranscript(
        IReadOnlyList<DiarizedSegment> segments,
        string fallbackText,
        bool diarize,
        double paragraphSeconds = MeetingRecord.Share.Helpers.MeetingMediaPolicy.TranscriptionSegmentSeconds)
    {
        if (segments.Count == 0)
        {
            return fallbackText;
        }

        var paragraphs = segments
            .GroupBy(x => (int)Math.Floor((x.Start ?? 0) / paragraphSeconds))
            .OrderBy(x => x.Key)
            .Select(group => diarize
                ? DiarizedTranscriptFormatter.Format(group.ToList(), string.Empty)
                : JoinPlain(group.Select(x => x.Text)))
            .Where(x => !string.IsNullOrWhiteSpace(x));

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>不分講者時一個段落接成一行：中文直接接，英數字之間補空白。</summary>
    private static string JoinPlain(IEnumerable<string> texts)
    {
        var builder = new StringBuilder();
        foreach (var text in texts)
        {
            if (builder.Length > 0 && char.IsAsciiLetterOrDigit(builder[^1]) && char.IsAsciiLetterOrDigit(text[0]))
            {
                builder.Append(' ');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    internal static Uri BuildRequestUri(string endpoint, string? apiVersion)
    {
        var baseAddress = endpoint.Trim().TrimEnd('/');
        var version = Uri.EscapeDataString(string.IsNullOrWhiteSpace(apiVersion) ? "2025-10-15" : apiVersion.Trim());
        return new Uri($"{baseAddress}/speechtotext/transcriptions:transcribe?api-version={version}");
    }

    /// <summary>失敗訊息：常見狀況講白話，其他附上狀態碼與回應開頭方便查。</summary>
    internal static string DescribeFailure(int statusCode, string? body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();
        if (detail.Length > 300)
        {
            detail = detail[..300] + "…";
        }

        var reason = statusCode switch
        {
            401 => "金鑰錯誤或不屬於這個 Speech 資源，請檢查 TranscriptionSettings:AzureSpeech:ApiKey。",
            403 => "沒有權限使用這個 Speech 資源（金鑰、網路限制或定價層不支援）。",
            404 => "找不到端點，請檢查 TranscriptionSettings:AzureSpeech:Endpoint 與 ApiVersion。",
            413 => "音檔太大，超過 LLM Speech 的上限。",
            429 => "呼叫太頻繁，被 Azure 限流，請稍後再試。",
            _ when detail.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                => "這個區域的 Speech 資源可能不支援 LLM Speech（只有部分區域提供）。",
            _ => "Azure LLM Speech 回應錯誤。",
        };

        return $"語音轉錄失敗（Azure LLM Speech，HTTP {statusCode}）：{reason}" + (detail.Length > 0 ? $" 回應：{detail}" : string.Empty);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static double ReadMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() / 1000d
            : 0;
}

/// <summary>1.1.73：一場 LLM Speech 轉錄的選項。</summary>
/// <param name="Diarize">要不要分講者。</param>
/// <param name="MaxSpeakers">分講者時的講者上限（2～35）；null 用 10。</param>
/// <param name="Phrases">片語清單（專案名稱＋與會人員名冊），提高人名與專有名詞的準確度。</param>
public sealed record LlmSpeechOptions(bool Diarize, int? MaxSpeakers, IReadOnlyList<string> Phrases);
