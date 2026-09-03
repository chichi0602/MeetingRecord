using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Configuration;

public static class StartupSafetyValidator
{
    private const string DevelopmentSigningKey = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars";
    private const string DevelopmentLlmApiKey = "DevelopmentOnly-ChangeThisLlmApiKey";
    private const string DevelopmentLlmEndpointMarker = "your-resource";

    /// <param name="ffmpegExists">
    /// FFmpeg 執行檔是否存在的判斷方式，預設為實際檢查檔案系統與 PATH。
    /// 開放覆寫是為了讓測試不必依賴執行機器上真的裝了 FFmpeg。
    /// </param>
    public static void Validate(
        IConfiguration configuration,
        string environmentName,
        Func<string, bool>? ffmpegExists = null)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errors = new List<string>();
        var signingKey = configuration[$"{JwtSettings.SectionName}:SigningKey"];
        if (string.Equals(signingKey, DevelopmentSigningKey, StringComparison.Ordinal))
        {
            errors.Add("JwtSettings:SigningKey 不可在 Production 使用開發預設值。");
        }

        var supportPassword = configuration["BootstrapSettings:SupportPassword"];
        if (string.Equals(supportPassword, "support", StringComparison.Ordinal))
        {
            errors.Add("BootstrapSettings:SupportPassword 不可在 Production 使用預設密碼。");
        }

        if (string.IsNullOrWhiteSpace(configuration[$"{SwaggerSettings.SectionName}:EnabledInProduction"]))
        {
            errors.Add("Swagger:EnabledInProduction 必須在 Production 明確設定 true 或 false。");
        }

        var cacheProvider = configuration[$"{CacheSettings.SectionName}:Provider"];
        if (string.Equals(cacheProvider, nameof(CacheProvider.Redis), StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(configuration[$"{CacheSettings.SectionName}:RedisConnection"]))
        {
            errors.Add("CacheSettings:RedisConnection 在 Production 使用 Redis provider 時不可留空。");
        }

        // LLM 設定：未指定 DefaultProvider 代表未啟用 LLM 功能，整段跳過，
        // 避免不使用 LLM 的部署因此無法上線。
        var llmDefaultProvider = configuration[$"{LlmSettings.SectionName}:DefaultProvider"];
        if (!string.IsNullOrWhiteSpace(llmDefaultProvider))
        {
            var providerName = llmDefaultProvider.Trim();
            var providerPath = $"{LlmSettings.SectionName}:Providers:{providerName}";

            var llmApiKey = configuration[$"{providerPath}:ApiKey"];
            if (string.IsNullOrWhiteSpace(llmApiKey))
            {
                errors.Add($"{providerPath}:ApiKey 在 Production 指定 {LlmSettings.SectionName}:DefaultProvider 後不可留空（請以環境變數 {LlmSettings.SectionName}__Providers__{providerName}__ApiKey 提供）。");
            }
            else if (string.Equals(llmApiKey, DevelopmentLlmApiKey, StringComparison.Ordinal))
            {
                errors.Add($"{providerPath}:ApiKey 不可在 Production 使用開發預設值。");
            }

            var llmEndpoint = configuration[$"{providerPath}:Endpoint"];
            if (string.IsNullOrWhiteSpace(llmEndpoint)
                || llmEndpoint.Contains(DevelopmentLlmEndpointMarker, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{providerPath}:Endpoint 不可在 Production 留空或沿用開發範例值。");
            }
        }

        var (transcriptionEnabled, effectiveTranscriptionProvider, transcriptionPath) =
            GetTranscriptionContext(configuration);

        if (!string.IsNullOrWhiteSpace(effectiveTranscriptionProvider))
        {
            var transcriptionModel = configuration[$"{transcriptionPath}:TranscriptionModel"];

            if (transcriptionEnabled && string.IsNullOrWhiteSpace(transcriptionModel))
            {
                errors.Add($"{transcriptionPath}:TranscriptionModel 在 Production 啟用語音轉錄後不可留空（例如 gpt-4o-transcribe）。");
            }

            // 轉錄供應商若與生成端不同，其 ApiKey／Endpoint 尚未被上面的區塊檢查過。
            if (transcriptionEnabled
                && !string.Equals(effectiveTranscriptionProvider, llmDefaultProvider?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var transcriptionApiKey = configuration[$"{transcriptionPath}:ApiKey"];
                if (string.IsNullOrWhiteSpace(transcriptionApiKey))
                {
                    errors.Add($"{transcriptionPath}:ApiKey 在 Production 指定 {LlmSettings.SectionName}:TranscriptionProvider 後不可留空（請以環境變數 {LlmSettings.SectionName}__Providers__{effectiveTranscriptionProvider}__ApiKey 提供）。");
                }
                else if (string.Equals(transcriptionApiKey, DevelopmentLlmApiKey, StringComparison.Ordinal))
                {
                    errors.Add($"{transcriptionPath}:ApiKey 不可在 Production 使用開發預設值。");
                }

                var transcriptionEndpoint = configuration[$"{transcriptionPath}:Endpoint"];
                if (string.IsNullOrWhiteSpace(transcriptionEndpoint)
                    || transcriptionEndpoint.Contains(DevelopmentLlmEndpointMarker, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{transcriptionPath}:Endpoint 不可在 Production 留空或沿用開發範例值。");
                }
            }

            // 轉錄前必須先以 FFmpeg 轉檔，路徑沒設或指到不存在的檔案都一定跑不動。
            if (transcriptionEnabled)
            {
                var ffmpegPath = configuration[$"{MediaSettings.SectionName}:FfmpegPath"];
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    errors.Add($"{MediaSettings.SectionName}:FfmpegPath 在 Production 啟用語音轉錄後不可留空。");
                }
                else if (!ResolveFfmpegExists(ffmpegExists)(ffmpegPath))
                {
                    errors.Add($"{MediaSettings.SectionName}:FfmpegPath 指向的 FFmpeg 執行檔不存在（{ffmpegPath}），Production 啟用語音轉錄後無法運作。");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Production 啟動安全檢查失敗：" + string.Join(" ", errors));
        }
    }


    /// <summary>
    /// 非 Production 的啟動提醒：這些設定會讓功能在執行時失敗，但不足以阻擋啟動。
    /// 沒在用這些功能的開發者不該因此無法啟動，所以只回訊息、不擲例外。
    /// </summary>
    /// <param name="ffmpegExists">FFmpeg 存在性判斷，開放覆寫是為了讓測試不依賴執行機器的安裝狀態。</param>
    /// <param name="browserExists">瀏覽器存在性判斷，同上。</param>
    public static IReadOnlyList<string> GetDevelopmentWarnings(
        IConfiguration configuration,
        Func<string, bool>? ffmpegExists = null,
        Func<string, bool>? browserExists = null)
    {
        var warnings = new List<string>();

        // 語音轉錄：沒啟用就整段跳過。
        if (GetTranscriptionContext(configuration).Enabled)
        {
            var ffmpegPath = configuration[$"{MediaSettings.SectionName}:FfmpegPath"];
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                warnings.Add($"{MediaSettings.SectionName}:FfmpegPath 未設定，語音轉錄會在執行時失敗。");
            }
            else if (!ResolveFfmpegExists(ffmpegExists)(ffmpegPath))
            {
                warnings.Add($"{MediaSettings.SectionName}:FfmpegPath 找不到 FFmpeg 執行檔（目前值：{ffmpegPath}），語音轉錄會在執行時失敗。");
            }
        }

        // 匯出 PDF：與轉錄無關，只要有草稿就能用，所以一律檢查。
        var browserPath = configuration[$"{ExportSettings.SectionName}:BrowserPath"];
        if (BrowserPathResolver.Resolve(browserPath, browserExists) is null)
        {
            warnings.Add(string.IsNullOrWhiteSpace(browserPath)
                ? $"找不到可用來產生 PDF 的瀏覽器（已試過 Edge 與 Chrome 的常見安裝位置），匯出會議紀錄 PDF 會在執行時失敗。可於 {ExportSettings.SectionName}:BrowserPath 指定路徑。"
                : $"{ExportSettings.SectionName}:BrowserPath 找不到瀏覽器執行檔（目前值：{browserPath}），匯出會議紀錄 PDF 會在執行時失敗。");
        }

        return warnings;
    }

    private static Func<string, bool> ResolveFfmpegExists(Func<string, bool>? ffmpegExists)
        => ffmpegExists ?? FfmpegPathResolver.Exists;

    /// <summary>
    /// 這份設定是否啟用了語音轉錄，以及該供應商的設定路徑前綴。
    /// 兩種都算「已啟用」——明確指定了 TranscriptionProvider，
    /// 或沿用 DefaultProvider 且該供應商填了 TranscriptionModel。
    /// 只用 LLM、不用轉錄的部署整段跳過，不該被轉錄相關檢查擋住。
    /// </summary>
    private static (bool Enabled, string ProviderName, string ProviderPath) GetTranscriptionContext(
        IConfiguration configuration)
    {
        var defaultProvider = configuration[$"{LlmSettings.SectionName}:DefaultProvider"];
        var transcriptionProvider = configuration[$"{LlmSettings.SectionName}:TranscriptionProvider"];

        var effectiveProvider = !string.IsNullOrWhiteSpace(transcriptionProvider)
            ? transcriptionProvider.Trim()
            : defaultProvider?.Trim();

        if (string.IsNullOrWhiteSpace(effectiveProvider))
        {
            return (false, string.Empty, string.Empty);
        }

        var providerPath = $"{LlmSettings.SectionName}:Providers:{effectiveProvider}";
        var enabled = !string.IsNullOrWhiteSpace(transcriptionProvider)
            || !string.IsNullOrWhiteSpace(configuration[$"{providerPath}:TranscriptionModel"]);

        return (enabled, effectiveProvider, providerPath);
    }
}
