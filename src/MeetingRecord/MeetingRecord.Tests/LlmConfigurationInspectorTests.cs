using Microsoft.Extensions.Configuration;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Tests;

/// <summary>LlmSettings 不認得的鍵與別名衝突（1.0.35）。設定繫結會靜默忽略打錯的鍵，這裡把它們找出來。</summary>
public sealed class LlmConfigurationInspectorTests
{
    /// <summary>1.0.34 部署到 IIS 的實際設定：Deployment 現在是 Model 的別名，不算未知。</summary>
    [Fact]
    public void DeploymentAliases_ShouldBeKnown()
    {
        var findings = Inspect(new()
        {
            ["LlmSettings:DefaultProvider"] = "AzureOpenAI",
            ["LlmSettings:Providers:AzureOpenAI:Endpoint"] = "https://kmengineopenai1.services.ai.azure.com",
            ["LlmSettings:Providers:AzureOpenAI:ApiKey"] = "",
            ["LlmSettings:Providers:AzureOpenAI:Deployment"] = "gpt-5.6-sol",
            ["LlmSettings:Providers:AzureOpenAI:TranscriptionDeployment"] = "gpt-transcribe",
        });

        Assert.False(findings.HasAny);
    }

    [Fact]
    public void LegacyApiVersion_ShouldBeKnown()
    {
        Assert.False(Inspect(new() { ["LlmSettings:Providers:AzureOpenAI:ApiVersion"] = "2024-10-21" }).HasAny);
    }

    [Fact]
    public void UnknownProviderKey_ShouldBeReportedWithSuggestion_CaseInsensitively()
    {
        var findings = Inspect(new()
        {
            ["LlmSettings:Providers:AzureOpenAI:modelname"] = "gpt-5.6-sol",
            ["LlmSettings:Providers:AzureOpenAI:Temperature"] = "0.2",
            ["LlmSettings:Providers:AzureOpenAI:ENDPOINT"] = "https://x.openai.azure.com",
        });

        Assert.Equal(2, findings.UnknownKeys.Count);
        Assert.Contains(findings.UnknownKeys, x => x.Contains("modelname", StringComparison.Ordinal) && x.Contains("是不是要寫 Model", StringComparison.Ordinal));
        Assert.Contains(findings.UnknownKeys, x => x.Contains("Temperature", StringComparison.Ordinal) && x.Contains("會被忽略", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownRootKey_ShouldBeReported()
    {
        var findings = Inspect(new() { ["LlmSettings:Provider"] = "AzureOpenAI" });

        Assert.Contains(findings.UnknownKeys, x => x.StartsWith("LlmSettings:Provider ", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelAndDeploymentBothSetAndDifferent_ShouldBeAConflict()
    {
        var findings = Inspect(new()
        {
            ["LlmSettings:Providers:AzureOpenAI:Model"] = "gpt-4o-mini",
            ["LlmSettings:Providers:AzureOpenAI:Deployment"] = "gpt-5.6-sol",
            ["LlmSettings:Providers:AzureOpenAI:TranscriptionModel"] = "gpt-transcribe",
            ["LlmSettings:Providers:AzureOpenAI:TranscriptionDeployment"] = "gpt-transcribe",
        });

        var conflict = Assert.Single(findings.AliasConflicts);
        Assert.Contains("採用 Model＝gpt-4o-mini", conflict);
        Assert.Contains("忽略 Deployment＝gpt-5.6-sol", conflict);
    }

    private static LlmConfigurationFindings Inspect(Dictionary<string, string?> values)
        => LlmConfigurationInspector.Inspect(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
