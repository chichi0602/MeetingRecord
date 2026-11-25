namespace MeetingRecord.Models.Systems;

/// <summary>
/// 1.1.73：影音轉文字稿的引擎選項——上傳／重新轉錄確認時的預設值，以及 Azure LLM Speech 的連線設定。
/// Azure OpenAI 的連線與部署仍在 <see cref="LlmSettings"/>；單價一律在 <see cref="LlmSettings.Pricing"/>
/// （LLM Speech 的鍵是 <see cref="AzureSpeechSettings.PricingModel"/>）。
/// </summary>
public class TranscriptionSettings
{
    public const string SectionName = "TranscriptionSettings";

    /// <summary>確認框預設選的引擎：<c>AzureOpenAI</c> 或 <c>AzureLlmSpeech</c>。該引擎沒設定好時畫面改選另一個。</summary>
    public string DefaultEngine { get; set; } = "AzureOpenAI";

    /// <summary>確認框「分講者」的預設值。</summary>
    public bool DefaultDiarize { get; set; } = true;

    /// <summary>沒有專案（或專案名冊是空的）時的講者上限預設值（2～35）。</summary>
    public int DefaultMaxSpeakers { get; set; } = 10;

    public AzureSpeechSettings AzureSpeech { get; set; } = new();
}

/// <summary>
/// Azure AI Speech（LLM Speech）的連線設定。<see cref="Endpoint"/> 與 <see cref="ApiKey"/> 都填了才會出現這個引擎。
///
/// <para>
/// <see cref="ApiKey"/> 為機敏值：正式環境請用環境變數 <c>TranscriptionSettings__AzureSpeech__ApiKey</c> 或 user-secrets。
/// LLM Speech 只在部分區域提供（eastus、westus、westus2、westeurope、northeurope、southeastasia、centralindia），
/// Endpoint 填哪個區域的 Speech 資源就送到哪裡，程式不寫死。
/// </para>
/// </summary>
public class AzureSpeechSettings
{
    /// <summary>用量帳本與 <see cref="LlmSettings.Pricing"/> 的鍵。LLM Speech 和 Fast Transcription 共用同一個計價項目。</summary>
    public const string PricingModel = "azure-llm-speech";

    /// <summary>API 允許的講者上限範圍。</summary>
    public const int MinSpeakers = 2;

    public const int MaxSpeakers = 35;

    /// <summary>Speech 資源的端點，例如 <c>https://your-resource.cognitiveservices.azure.com/</c>。</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>辨識語言。預設台灣中文。</summary>
    public string Locale { get; set; } = "zh-TW";

    /// <summary>Fast Transcription API 的版本。</summary>
    public string ApiVersion { get; set; } = "2025-10-15";

    /// <summary>
    /// 單檔送出的長度上限（秒）。官方配額頁寫 5 小時，但 REST 參考寫 2 小時，保守取 2 小時；
    /// 超過就在停頓處切成幾段分別送，每段的講者各自編號（逐字稿標成「講者 2-1」）。
    /// </summary>
    public int MaxFileSeconds { get; set; } = 7200;

    /// <summary>單次請求的逾時（分鐘）。整段一次送、等全部結果回來，長會議要夠長。</summary>
    public int TimeoutMinutes { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
