using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.Systems;

/// <summary>
/// 大語言模型（LLM）與語音轉錄（STT）供應商設定。
///
/// 以 <see cref="Providers"/> 字典依「供應商名稱」分組，例如 <c>AzureOpenAI</c>；
/// 未來要加入其他廠商（如 <c>GoogleGemini</c>）只需新增一個項目並切換
/// <see cref="DefaultProvider"/> 或 <see cref="TranscriptionProvider"/>，既有設定鍵不需變動。
///
/// <para>
/// 生成端（<see cref="DefaultProvider"/>）與轉錄端（<see cref="TranscriptionProvider"/>）
/// 可指向不同供應商；<see cref="TranscriptionProvider"/> 留空時沿用 <see cref="DefaultProvider"/>。
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
    /// 語音轉錄使用的供應商名稱。留空時沿用 <see cref="DefaultProvider"/>；
    /// 兩者皆留空代表尚未啟用語音轉錄功能。
    /// </summary>
    public string TranscriptionProvider { get; set; } = string.Empty;

    /// <summary>
    /// 各供應商設定，鍵為供應商名稱（AzureOpenAI、GoogleGemini…）。
    /// </summary>
    public Dictionary<string, LlmProviderSettings> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 估算金額的幣別代碼。**只用於顯示，不做匯率換算**——Azure 的公告價與帳單都是美金，
    /// 多一道匯率只會讓這頁的數字與帳單對不起來。
    /// </summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// 用量單價表，鍵為 <b>deployment／model 名稱</b>（對應
    /// <see cref="LlmProviderSettings.Model"/> 與 <see cref="LlmProviderSettings.TranscriptionModel"/>，
    /// 也是用量帳本 <c>AiUsageLog.Model</c> 記下的字串，三者對得起來）。
    ///
    /// <para>
    /// 查不到或欄位為 null 時，帳本的金額欄位**留空白而不是 0**——0 會讓總額靜靜地少報。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <see cref="StringComparer.OrdinalIgnoreCase"/> 必須寫在這個屬性初始化器裡
    /// （與 <see cref="Providers"/> 同樣的寫法）。Configuration binder 是往既有實例裡塞，
    /// comparer 會保留；寫成 <c>= new()</c> 就變成大小寫敏感，
    /// <c>GPT-4o-mini</c> 查不到單價而靜靜地不計費。
    /// </para>
    /// </summary>
    public Dictionary<string, LlmPricingSettings> Pricing { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已指定預設供應商。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(DefaultProvider);

    /// <summary>實際生效的轉錄供應商名稱（<see cref="TranscriptionProvider"/> 優先，其次 <see cref="DefaultProvider"/>）。</summary>
    public string EffectiveTranscriptionProviderName =>
        !string.IsNullOrWhiteSpace(TranscriptionProvider)
            ? TranscriptionProvider.Trim()
            : DefaultProvider.Trim();

    /// <summary>
    /// 是否已具備可用的轉錄設定：找得到供應商，且該供應商填了 <see cref="LlmProviderSettings.TranscriptionModel"/>。
    /// 只設定 <see cref="DefaultProvider"/>（用 LLM 但不用轉錄）的部署會是 false。
    /// </summary>
    public bool IsTranscriptionConfigured =>
        !string.IsNullOrWhiteSpace(EffectiveTranscriptionProviderName)
        && Providers.TryGetValue(EffectiveTranscriptionProviderName, out var provider)
        && !string.IsNullOrWhiteSpace(provider.TranscriptionModel);

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
    /// 取得語音轉錄供應商設定。未指定任何供應商時回傳 null；
    /// 已指定但查無對應項目時擲出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public LlmProviderSettings? GetTranscriptionProvider()
    {
        if (!IsTranscriptionConfigured)
        {
            return null;
        }

        var name = EffectiveTranscriptionProviderName;
        if (!Providers.TryGetValue(name, out var provider))
        {
            throw new InvalidOperationException(
                $"{SectionName} 指定的轉錄供應商「{name}」不存在於 {SectionName}:Providers。");
        }

        return provider;
    }

    /// <summary>
    /// 只驗證「結構一致性」：指定了供應商時，該供應商必須存在且必要欄位不可空白。
    /// 刻意不驗證 <see cref="LlmProviderSettings.ApiKey"/>——正式環境由環境變數注入，
    /// 啟動驗證時可能尚未提供，該檢查改由 Production 專用的啟動安全檢查負責。
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // ⚠️ 單價檢查必須放在最前面：下面的分支有 yield break，
        // 放在後面時「沒有指定 TranscriptionProvider」的部署就永遠檢查不到。
        foreach (var (model, price) in Pricing)
        {
            // 負單價會產生負成本，而負成本在圖表上只是看起來「這個月比較省」——
            // 沒有任何跡象顯示是設定打錯了。
            foreach (var (field, value) in new (string, decimal?)[]
                     {
                         (nameof(LlmPricingSettings.InputPerMillionTokens), price.InputPerMillionTokens),
                         (nameof(LlmPricingSettings.OutputPerMillionTokens), price.OutputPerMillionTokens),
                         (nameof(LlmPricingSettings.AudioPerMinute), price.AudioPerMinute),
                     })
            {
                if (value < 0)
                {
                    yield return new ValidationResult(
                        $"{SectionName}:Pricing:{model}:{field} 不可為負數（目前為 {value}）。",
                        [nameof(Pricing)]);
                }
            }
        }

        if (IsConfigured)
        {
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

        // 轉錄段只在「明確指定 TranscriptionProvider」時驗證。
        // 只設 DefaultProvider 的部署代表沒打算用轉錄，不該被這裡擋住啟動。
        if (string.IsNullOrWhiteSpace(TranscriptionProvider))
        {
            yield break;
        }

        var transcriptionName = TranscriptionProvider.Trim();
        if (!Providers.TryGetValue(transcriptionName, out var transcriptionProvider))
        {
            yield return new ValidationResult(
                $"{SectionName}:TranscriptionProvider 指定的供應商「{transcriptionName}」不存在於 {SectionName}:Providers。",
                [nameof(TranscriptionProvider)]);
            yield break;
        }

        // 轉錄供應商的 Endpoint／ApiVersion 與生成端共用，上方已檢查；這裡只補轉錄專屬欄位。
        if (string.IsNullOrWhiteSpace(transcriptionProvider.TranscriptionModel))
        {
            yield return new ValidationResult(
                $"{SectionName}:Providers:{transcriptionName}:TranscriptionModel 不可為空白（語音轉錄的部署／模型名稱）。",
                [nameof(Providers)]);
        }
    }
}

/// <summary>
/// 單一 deployment／model 的用量單價，供「AI 用量分析」估算金額。
///
/// <para>
/// 全部欄位可為 null：只設了文字生成單價、沒設轉錄單價是正常狀態。
/// 缺少必要單價時該筆帳的金額會留空白，**不會被當成 0**。
/// </para>
/// </summary>
public class LlmPricingSettings
{
    /// <summary>每百萬輸入 token 的單價。</summary>
    public decimal? InputPerMillionTokens { get; set; }

    /// <summary>每百萬輸出 token 的單價。通常是輸入的 3～4 倍。</summary>
    public decimal? OutputPerMillionTokens { get; set; }

    /// <summary>每分鐘音訊的單價（語音轉錄用）。</summary>
    public decimal? AudioPerMinute { get; set; }
}

/// <summary>
/// 單一 LLM／STT 供應商的連線設定。
/// </summary>
public class LlmProviderSettings
{
    /// <summary>API 端點，例如 https://your-resource.openai.azure.com/ 。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API 金鑰（機敏值，正式環境以環境變數或 user-secrets 提供）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>文字生成用：Azure OpenAI 為 deployment 名稱；其他廠商為 model id。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>API 版本，例如 Azure OpenAI 的 2024-10-21。</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    /// 語音轉錄用的 deployment／model 名稱，例如 <c>gpt-4o-transcribe</c>。
    /// 未啟用轉錄功能時可留空。
    /// </summary>
    public string TranscriptionModel { get; set; } = string.Empty;

    /// <summary>
    /// 語音轉錄用的 API 版本。留空時沿用 <see cref="ApiVersion"/>；
    /// 轉錄端點通常需要比文字生成更新的版本，因此獨立成一個欄位。
    /// </summary>
    public string TranscriptionApiVersion { get; set; } = string.Empty;

    /// <summary>實際送出轉錄請求時使用的 API 版本。</summary>
    public string EffectiveTranscriptionApiVersion =>
        !string.IsNullOrWhiteSpace(TranscriptionApiVersion) ? TranscriptionApiVersion.Trim() : ApiVersion.Trim();
}
