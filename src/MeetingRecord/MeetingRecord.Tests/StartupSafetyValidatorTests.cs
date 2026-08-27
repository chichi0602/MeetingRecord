using Microsoft.Extensions.Configuration;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Tests;

public sealed class StartupSafetyValidatorTests
{
    private const string DevelopmentLlmApiKey = "DevelopmentOnly-ChangeThisLlmApiKey";

    [Fact]
    public void Validate_NonProduction_ShouldNeverThrow()
    {
        // 開發環境即使全部沿用預設值也不應阻擋啟動。
        var configuration = Build(new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["LlmSettings:DefaultProvider"] = "AzureOpenAI",
            ["LlmSettings:Providers:AzureOpenAI:ApiKey"] = DevelopmentLlmApiKey,
            ["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://your-resource.openai.azure.com/",
        });

        StartupSafetyValidator.Validate(configuration, "Development");
    }

    [Fact]
    public void Validate_Production_WithoutLlmSection_ShouldNotThrow()
    {
        // 未指定 DefaultProvider 代表未啟用 LLM，整段檢查跳過。
        StartupSafetyValidator.Validate(Build(BaseValidProductionConfig()), "Production");
    }

    [Fact]
    public void Validate_Production_WithBlankDefaultProvider_ShouldNotThrow()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = string.Empty;
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = string.Empty;

        StartupSafetyValidator.Validate(Build(settings), "Production");
    }

    [Fact]
    public void Validate_Production_WithValidLlmSettings_ShouldNotThrow()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "real-production-key";
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com/";

        StartupSafetyValidator.Validate(Build(settings), "Production");
    }

    [Fact]
    public void Validate_Production_WithDefaultProviderButBlankApiKey_ShouldThrow()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = string.Empty;
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com/";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void Validate_Production_WithDevelopmentLlmApiKey_ShouldThrow()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = DevelopmentLlmApiKey;
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com/";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("開發預設值", ex.Message);
    }

    [Fact]
    public void Validate_Production_WithPlaceholderEndpoint_ShouldThrow()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "real-production-key";
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://your-resource.openai.azure.com/";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("Endpoint", ex.Message);
    }

    #region 語音轉錄設定

    [Fact]
    public void Validate_Production_WithLlmButNoTranscription_ShouldNotThrow()
    {
        // 只用 LLM、不用語音轉錄的部署不該被轉錄檢查擋住上線。
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "real-production-key";
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com/";

        StartupSafetyValidator.Validate(Build(settings), "Production");
    }

    [Fact]
    public void Validate_Production_WithValidTranscriptionSettings_ShouldNotThrow()
    {
        StartupSafetyValidator.Validate(Build(ValidTranscriptionProductionConfig()), "Production");
    }

    [Fact]
    public void Validate_Production_WithTranscriptionProviderButBlankModel_ShouldThrow()
    {
        var settings = ValidTranscriptionProductionConfig();
        settings["LlmSettings:Providers:AzureOpenAI:TranscriptionModel"] = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("TranscriptionModel", ex.Message);
    }

    [Fact]
    public void Validate_Production_WithTranscriptionEnabledButBlankFfmpegPath_ShouldThrow()
    {
        // 轉錄前一定要先經 FFmpeg 轉檔，路徑沒設就必定失敗，寧可在啟動時擋下。
        var settings = ValidTranscriptionProductionConfig();
        settings["MediaSettings:FfmpegPath"] = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("FfmpegPath", ex.Message);
    }

    [Fact]
    public void Validate_Production_WithSeparateTranscriptionProvider_ShouldCheckItsOwnApiKey()
    {
        var settings = ValidTranscriptionProductionConfig();
        settings["LlmSettings:TranscriptionProvider"] = "OtherVendor";
        settings["LlmSettings:Providers:OtherVendor:Endpoint"] = "https://stt.example.com/";
        settings["LlmSettings:Providers:OtherVendor:TranscriptionModel"] = "stt-1";
        settings["LlmSettings:Providers:OtherVendor:ApiKey"] = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("OtherVendor", ex.Message);
    }

    [Fact]
    public void Validate_Production_WithTranscriptionEnabledByDefaultProviderModel_ShouldRequireFfmpegPath()
    {
        // 沒寫 TranscriptionProvider，但 DefaultProvider 填了 TranscriptionModel，一樣算啟用轉錄。
        var settings = ValidTranscriptionProductionConfig();
        settings.Remove("LlmSettings:TranscriptionProvider");
        settings["MediaSettings:FfmpegPath"] = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupSafetyValidator.Validate(Build(settings), "Production"));
        Assert.Contains("FfmpegPath", ex.Message);
    }

    private static Dictionary<string, string?> ValidTranscriptionProductionConfig()
    {
        var settings = BaseValidProductionConfig();
        settings["LlmSettings:DefaultProvider"] = "AzureOpenAI";
        settings["LlmSettings:TranscriptionProvider"] = "AzureOpenAI";
        settings["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "real-production-key";
        settings["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com/";
        settings["LlmSettings:Providers:AzureOpenAI:TranscriptionModel"] = "gpt-4o-transcribe";
        settings["MediaSettings:FfmpegPath"] = @"C:\ffmpeg\bin\ffmpeg.exe";
        return settings;
    }

    #endregion

    /// <summary>
    /// Validate 會累積所有錯誤後一次擲出，因此測 LLM 規則時其他規則必須先通過。
    /// </summary>
    private static Dictionary<string, string?> BaseValidProductionConfig() => new()
    {
        ["JwtSettings:SigningKey"] = "ProductionSigningKey-AtLeast32Characters-Long",
        ["BootstrapSettings:SupportPassword"] = "a-real-production-password",
        ["Swagger:EnabledInProduction"] = "false",
        ["CacheSettings:Provider"] = "Memory",
    };

    private static IConfiguration Build(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
