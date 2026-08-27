using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class LlmSettingsTests
{
    [Fact]
    public void Bind_ShouldPopulateProviderDictionary()
    {
        var settings = Bind(BaseSettings());

        Assert.Equal("AzureOpenAI", settings.DefaultProvider);
        var provider = Assert.Single(settings.Providers).Value;
        Assert.Equal("https://demo.openai.azure.com/", provider.Endpoint);
        Assert.Equal("dev-key", provider.ApiKey);
        Assert.Equal("gpt-4o-mini", provider.Model);
        Assert.Equal("2024-10-21", provider.ApiVersion);
    }

    [Fact]
    public void Bind_ShouldOverrideOnlyApiKey_WhenLaterSourceSetsThatKey()
    {
        // 對應 appsettings.Production.json 只挖空 ApiKey、再由環境變數注入的合併語意。
        var settings = Bind(
            BaseSettings(),
            new Dictionary<string, string?>
            {
                ["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "production-key",
            });

        var provider = settings.Providers["AzureOpenAI"];
        Assert.Equal("production-key", provider.ApiKey);
        Assert.Equal("https://demo.openai.azure.com/", provider.Endpoint);
        Assert.Equal("gpt-4o-mini", provider.Model);
        Assert.Equal("2024-10-21", provider.ApiVersion);
    }

    [Fact]
    public void Providers_LookupShouldBeCaseInsensitive()
    {
        var settings = Bind(BaseSettings());

        Assert.True(settings.Providers.ContainsKey("azureopenai"));
        Assert.Equal("gpt-4o-mini", settings.Providers["AZUREOPENAI"].Model);
    }

    [Fact]
    public void Bind_ShouldSupportMultipleProviders()
    {
        var config = BaseSettings();
        config["LlmSettings:Providers:GoogleGemini:Endpoint"] = "https://generativelanguage.googleapis.com/";
        config["LlmSettings:Providers:GoogleGemini:Model"] = "gemini-2.0-flash";
        config["LlmSettings:Providers:GoogleGemini:ApiVersion"] = "v1beta";

        var settings = Bind(config);

        Assert.Equal(2, settings.Providers.Count);
        Assert.Equal("gemini-2.0-flash", settings.Providers["GoogleGemini"].Model);
    }

    [Fact]
    public void GetDefaultProvider_ShouldReturnNull_WhenDefaultProviderIsBlank()
    {
        var settings = new LlmSettings();

        Assert.False(settings.IsConfigured);
        Assert.Null(settings.GetDefaultProvider());
    }

    [Fact]
    public void GetDefaultProvider_ShouldThrow_WhenDefaultProviderNotInDictionary()
    {
        var settings = new LlmSettings { DefaultProvider = "NotConfigured" };

        Assert.Throws<InvalidOperationException>(() => settings.GetDefaultProvider());
    }

    [Fact]
    public void Validate_ShouldPass_WhenSectionIsEmpty()
    {
        // 未啟用 LLM 的部署必須能通過啟動驗證，否則會擋住不使用 LLM 的環境。
        Assert.Empty(Validate(new LlmSettings()));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenDefaultProviderMissingFromProviders()
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI" };

        var results = Validate(settings);

        Assert.Single(results);
    }

    [Fact]
    public void Validate_ShouldReportError_WhenRequiredProviderFieldsAreBlank()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers = { ["AzureOpenAI"] = new LlmProviderSettings() },
        };

        var results = Validate(settings);

        // Endpoint / Model / ApiVersion 各一筆
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Validate_ShouldNotRequireApiKey()
    {
        // 刻意設計：ApiKey 在正式環境由環境變數注入，改由 StartupSafetyValidator 檢查。
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings
                {
                    Endpoint = "https://demo.openai.azure.com/",
                    Model = "gpt-4o-mini",
                    ApiVersion = "2024-10-21",
                    ApiKey = string.Empty,
                },
            },
        };

        Assert.Empty(Validate(settings));
    }

    #region 語音轉錄段

    [Fact]
    public void EffectiveTranscriptionProviderName_ShouldFallBackToDefaultProvider()
    {
        var settings = new LlmSettings { DefaultProvider = "AzureOpenAI" };

        Assert.Equal("AzureOpenAI", settings.EffectiveTranscriptionProviderName);
    }

    [Fact]
    public void EffectiveTranscriptionProviderName_ShouldPreferTranscriptionProvider()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            TranscriptionProvider = "OtherVendor",
        };

        Assert.Equal("OtherVendor", settings.EffectiveTranscriptionProviderName);
    }

    [Fact]
    public void IsTranscriptionConfigured_ShouldBeFalse_WhenTranscriptionModelIsBlank()
    {
        // 只設 DefaultProvider（用 LLM 但不用轉錄）不算已啟用轉錄。
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers = { ["AzureOpenAI"] = new LlmProviderSettings { Model = "gpt-4o-mini" } },
        };

        Assert.False(settings.IsTranscriptionConfigured);
        Assert.Null(settings.GetTranscriptionProvider());
    }

    [Fact]
    public void GetTranscriptionProvider_ShouldReturnProvider_WhenTranscriptionModelIsSet()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings
                {
                    Model = "gpt-4o-mini",
                    TranscriptionModel = "gpt-4o-transcribe",
                },
            },
        };

        Assert.True(settings.IsTranscriptionConfigured);
        Assert.Equal("gpt-4o-transcribe", settings.GetTranscriptionProvider()!.TranscriptionModel);
    }

    [Fact]
    public void Validate_ShouldNotRequireTranscriptionModel_WhenTranscriptionProviderIsBlank()
    {
        // 未指定 TranscriptionProvider 代表沒打算用轉錄，不該擋住只用 LLM 的部署。
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings
                {
                    Endpoint = "https://demo.openai.azure.com/",
                    Model = "gpt-4o-mini",
                    ApiVersion = "2024-10-21",
                },
            },
        };

        Assert.Empty(Validate(settings));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenTranscriptionProviderMissingFromProviders()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            TranscriptionProvider = "NotConfigured",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings
                {
                    Endpoint = "https://demo.openai.azure.com/",
                    Model = "gpt-4o-mini",
                    ApiVersion = "2024-10-21",
                    TranscriptionModel = "gpt-4o-transcribe",
                },
            },
        };

        var result = Assert.Single(Validate(settings));
        Assert.Contains("TranscriptionProvider", result.ErrorMessage);
    }

    [Fact]
    public void Validate_ShouldReportError_WhenTranscriptionProviderHasBlankTranscriptionModel()
    {
        var settings = new LlmSettings
        {
            DefaultProvider = "AzureOpenAI",
            TranscriptionProvider = "AzureOpenAI",
            Providers =
            {
                ["AzureOpenAI"] = new LlmProviderSettings
                {
                    Endpoint = "https://demo.openai.azure.com/",
                    Model = "gpt-4o-mini",
                    ApiVersion = "2024-10-21",
                },
            },
        };

        var result = Assert.Single(Validate(settings));
        Assert.Contains("TranscriptionModel", result.ErrorMessage);
    }

    [Fact]
    public void Bind_ShouldPopulateTranscriptionFields()
    {
        var config = BaseSettings();
        config["LlmSettings:TranscriptionProvider"] = "AzureOpenAI";
        config["LlmSettings:Providers:AzureOpenAI:TranscriptionModel"] = "gpt-4o-transcribe";
        config["LlmSettings:Providers:AzureOpenAI:TranscriptionApiVersion"] = "2025-03-01-preview";

        var settings = Bind(config);

        Assert.Equal("AzureOpenAI", settings.TranscriptionProvider);
        var provider = settings.Providers["AzureOpenAI"];
        Assert.Equal("gpt-4o-transcribe", provider.TranscriptionModel);
        Assert.Equal("2025-03-01-preview", provider.EffectiveTranscriptionApiVersion);
    }

    #endregion

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["LlmSettings:DefaultProvider"] = "AzureOpenAI",
        ["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://demo.openai.azure.com/",
        ["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "dev-key",
        ["LlmSettings:Providers:AzureOpenAI:Model"] = "gpt-4o-mini",
        ["LlmSettings:Providers:AzureOpenAI:ApiVersion"] = "2024-10-21",
    };

    private static LlmSettings Bind(params Dictionary<string, string?>[] sources)
    {
        var builder = new ConfigurationBuilder();
        foreach (var source in sources)
        {
            builder.AddInMemoryCollection(source);
        }

        var settings = new LlmSettings();
        builder.Build().GetSection(LlmSettings.SectionName).Bind(settings);
        return settings;
    }

    private static List<ValidationResult> Validate(LlmSettings settings)
    {
        return settings.Validate(new ValidationContext(settings)).ToList();
    }
}
