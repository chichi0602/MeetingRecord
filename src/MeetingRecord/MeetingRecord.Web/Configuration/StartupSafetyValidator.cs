using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Web.Configuration;

public static class StartupSafetyValidator
{
    private const string DevelopmentSigningKey = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars";
    private const string DevelopmentLlmApiKey = "DevelopmentOnly-ChangeThisLlmApiKey";
    private const string DevelopmentLlmEndpointMarker = "your-resource";

    public static void Validate(IConfiguration configuration, string environmentName)
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

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Production 啟動安全檢查失敗：" + string.Join(" ", errors));
        }
    }
}
