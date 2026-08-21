using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 大語言模型（LLM）供應商設定。
///
/// 以 <see cref="Providers"/> 字典依「供應商名稱」分組，例如 <c>AzureOpenAI</c>；
/// 未來要加入其他廠商（如 <c>GoogleGemini</c>）只需新增一個項目並切換
/// <see cref="DefaultProvider"/>，既有設定鍵不需變動。
///
/// <para>
/// <b>0.4.26 現況：本設定僅為強型別骨架，程式尚未有任何呼叫端</b>，
/// 填入設定值不會產生任何外部請求。完整流程規劃見 docs/prd/會議紀錄產生流程-prd.md。
/// </para>
///
/// <para>
/// <c>ApiKey</c> 為機敏值：版控內只放開發預設值，正式環境請以環境變數
/// <c>LlmSettings__Providers__AzureOpenAI__ApiKey</c> 或 user-secrets 提供。
/// </para>
/// </summary>
public class LlmSettings : IValidatableObject
{
    public const string SectionName = "LlmSettings";

    /// <summary>
    /// 預設使用的供應商名稱（對應 <see cref="Providers"/> 的鍵）。留空代表尚未啟用 LLM 功能。
    /// </summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>
    /// 各供應商設定，鍵為供應商名稱（AzureOpenAI、GoogleGemini…）。
    /// </summary>
    public Dictionary<string, LlmProviderSettings> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已指定預設供應商。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(DefaultProvider);

    /// <summary>
    /// 取得預設供應商設定。未指定 <see cref="DefaultProvider"/> 時回傳 null；
    /// 已指定但查無對應項目時擲出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public LlmProviderSettings? GetDefaultProvider()
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (!Providers.TryGetValue(DefaultProvider.Trim(), out var provider))
        {
            throw new InvalidOperationException(
                $"{SectionName}:DefaultProvider 指定的供應商「{DefaultProvider}」不存在於 {SectionName}:Providers。");
        }

        return provider;
    }

    /// <summary>
    /// 只驗證「結構一致性」：指定了預設供應商時，該供應商必須存在且必要欄位不可空白。
    /// 刻意不驗證 <see cref="LlmProviderSettings.ApiKey"/>——正式環境由環境變數注入，
    /// 啟動驗證時可能尚未提供，該檢查改由 Production 專用的啟動安全檢查負責。
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsConfigured)
        {
            yield break;
        }

        var name = DefaultProvider.Trim();
        if (!Providers.TryGetValue(name, out var provider))
        {
            yield return new ValidationResult(
                $"{SectionName}:DefaultProvider 指定的供應商「{name}」不存在於 {SectionName}:Providers。",
                [nameof(DefaultProvider)]);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            yield return new ValidationResult(
                $"{SectionName}:Providers:{name}:Endpoint 不可為空白。",
                [nameof(Providers)]);
        }

        if (string.IsNullOrWhiteSpace(provider.Model))
        {
            yield return new ValidationResult(
                $"{SectionName}:Providers:{name}:Model 不可為空白。",
                [nameof(Providers)]);
        }

        if (string.IsNullOrWhiteSpace(provider.ApiVersion))
        {
            yield return new ValidationResult(
                $"{SectionName}:Providers:{name}:ApiVersion 不可為空白。",
                [nameof(Providers)]);
        }
    }
}

/// <summary>
/// 單一 LLM 供應商的連線設定。
/// </summary>
public class LlmProviderSettings
{
    /// <summary>API 端點，例如 https://your-resource.openai.azure.com/ 。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API 金鑰（機敏值，正式環境以環境變數或 user-secrets 提供）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure OpenAI 為 deployment 名稱；其他廠商為 model id。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>API 版本，例如 Azure OpenAI 的 2024-10-21。</summary>
    public string ApiVersion { get; set; } = string.Empty;
}
