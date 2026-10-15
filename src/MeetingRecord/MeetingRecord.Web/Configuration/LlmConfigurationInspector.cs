using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Configuration;

/// <summary>
/// 檢查 <c>LlmSettings</c> 區段有沒有系統不認得的鍵、別名有沒有互相衝突（1.0.35）。
///
/// <para>為什麼需要：.NET 的設定繫結會<b>靜默忽略</b>不認得的鍵。1.0.34 以前把部署名稱寫成 <c>Deployment</c>，
/// <c>Model</c> 就悄悄退回 appsettings.json 的預設值，Azure 回 404 DeploymentNotFound，卻看不出是設定寫錯。
/// 1.0.35 起 <c>Deployment</c> 已是 <c>Model</c> 的別名，但其他打錯的鍵（例如 <c>ModelName</c>）仍會被忽略，
/// 由這裡找出來：啟動時寫警告日誌，健康監控的「AI 供應商設定」亮黃燈。</para>
///
/// <para>⚠️ 一定要讀原始 <see cref="IConfiguration"/>：繫結後的 <see cref="LlmSettings"/> 已經看不到被忽略的鍵。</para>
/// </summary>
public static class LlmConfigurationInspector
{
    private static readonly HashSet<string> KnownRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(LlmSettings.DefaultProvider),
        nameof(LlmSettings.TranscriptionProvider),
        nameof(LlmSettings.Providers),
        nameof(LlmSettings.Currency),
        nameof(LlmSettings.Pricing),
    };

    private static readonly HashSet<string> KnownProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(LlmProviderSettings.Endpoint),
        nameof(LlmProviderSettings.ApiKey),
        nameof(LlmProviderSettings.Model),
        nameof(LlmProviderSettings.Deployment),
        nameof(LlmProviderSettings.TranscriptionModel),
        nameof(LlmProviderSettings.TranscriptionDeployment),
        nameof(LlmProviderSettings.TranscriptionApiVersion),

        // 1.0.28 以前文字生成要填的 api-version。v1 API 不需要，舊設定檔留著也照常啟動（LlmSettingsTests 守門）。
        "ApiVersion",
    };

    /// <summary>常見的打錯寫法 → 建議改成的鍵。</summary>
    private static readonly Dictionary<string, string> Suggestions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ModelName"] = "Model",
        ["DeploymentName"] = "Model",
        ["DeploymentId"] = "Model",
        ["TranscriptionModelName"] = "TranscriptionModel",
        ["TranscriptionDeploymentName"] = "TranscriptionModel",
        ["Key"] = "ApiKey",
        ["Url"] = "Endpoint",
        ["BaseUrl"] = "Endpoint",
    };

    public static LlmConfigurationFindings Inspect(IConfiguration configuration)
    {
        var section = configuration.GetSection(LlmSettings.SectionName);
        var unknown = new List<string>();
        var conflicts = new List<string>();

        foreach (var child in section.GetChildren())
        {
            if (!KnownRootKeys.Contains(child.Key))
            {
                unknown.Add(Describe($"{LlmSettings.SectionName}:{child.Key}", child.Key));
            }
        }

        foreach (var provider in section.GetSection(nameof(LlmSettings.Providers)).GetChildren())
        {
            var path = $"{LlmSettings.SectionName}:{nameof(LlmSettings.Providers)}:{provider.Key}";
            foreach (var child in provider.GetChildren())
            {
                if (!KnownProviderKeys.Contains(child.Key))
                {
                    unknown.Add(Describe($"{path}:{child.Key}", child.Key));
                }
            }

            AddConflict(conflicts, provider, path, nameof(LlmProviderSettings.Model), nameof(LlmProviderSettings.Deployment));
            AddConflict(conflicts, provider, path, nameof(LlmProviderSettings.TranscriptionModel), nameof(LlmProviderSettings.TranscriptionDeployment));
        }

        return new LlmConfigurationFindings(unknown, conflicts);
    }

    private static string Describe(string path, string key)
        => Suggestions.TryGetValue(key, out var suggestion)
            ? $"{path} 系統不認得，是不是要寫 {suggestion}？"
            : $"{path} 系統不認得，會被忽略。";

    private static void AddConflict(List<string> conflicts, IConfigurationSection provider, string path, string key, string alias)
    {
        var value = provider[key];
        var aliasValue = provider[alias];
        if (!string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(aliasValue)
            && !string.Equals(value.Trim(), aliasValue.Trim(), StringComparison.Ordinal))
        {
            conflicts.Add($"{path} 的 {key} 與 {alias} 都有填而且不同：採用 {key}＝{value.Trim()}，忽略 {alias}＝{aliasValue.Trim()}。");
        }
    }
}

/// <summary><see cref="LlmConfigurationInspector"/> 的檢查結果。</summary>
public sealed record LlmConfigurationFindings(IReadOnlyList<string> UnknownKeys, IReadOnlyList<string> AliasConflicts)
{
    public static readonly LlmConfigurationFindings None = new([], []);

    public bool HasAny => UnknownKeys.Count > 0 || AliasConflicts.Count > 0;

    public IEnumerable<string> All => UnknownKeys.Concat(AliasConflicts);
}
