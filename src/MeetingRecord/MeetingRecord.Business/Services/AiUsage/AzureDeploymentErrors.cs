using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// Azure OpenAI 呼叫失敗時的錯誤訊息（1.0.35）。文字生成與轉錄兩個供應商共用。
///
/// <para>為什麼要特別處理 404 DeploymentNotFound：Azure 原文只說「部署不存在」，看不出系統送的是哪個部署名稱。
/// 1.0.34 以前把設定鍵寫成 <c>Deployment</c> 時，系統悄悄改用預設的 <c>gpt-4o-mini</c>，Azure 上明明有部署卻一直 404。
/// 訊息寫出「送出的部署名稱、端點主機、該檢查哪個設定鍵」，一眼就看得出問題在設定。</para>
///
/// <para>⚠️ 這段訊息會存進用量帳本與 AI 對話紀錄的錯誤欄位：只放主機名稱，不放金鑰、標頭或查詢字串。</para>
/// </summary>
public static class AzureDeploymentErrors
{
    private const int MaxBodyLength = 500;

    /// <summary>失敗回應的訊息。</summary>
    /// <param name="apiLabel">「文字生成」或「語音轉錄」。</param>
    /// <param name="settingKey">應檢查的設定鍵，例如 <c>LlmSettings:Providers:AzureOpenAI:Model</c>。</param>
    public static string Describe(
        string apiLabel,
        int statusCode,
        string? reasonPhrase,
        string? body,
        string deploymentName,
        string? endpoint,
        string settingKey)
    {
        var original = Truncate(body);
        if (statusCode == 404 && body is not null && body.Contains("DeploymentNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return $"{apiLabel} API 回應 404：找不到 Azure 部署「{deploymentName.Trim()}」（{Host(endpoint)}）。"
                + $"請確認 {settingKey}（或別名 {AliasOf(settingKey)}）填的是 Azure 上的「部署名稱」。原始回應：{original}";
        }

        return $"{apiLabel} API 回應 {statusCode} {reasonPhrase}：{original}";
    }

    /// <summary>部署名稱空白時的訊息：寫明要填哪個鍵。</summary>
    public static string MissingDeployment(string apiLabel, string settingKey)
        => $"尚未設定{apiLabel}的 Azure 部署名稱，請在 {settingKey}（或別名 {AliasOf(settingKey)}）填入。";

    public static string ProviderKey(string providerName, string key)
        => $"{LlmSettings.SectionName}:Providers:{(string.IsNullOrWhiteSpace(providerName) ? "（供應商）" : providerName.Trim())}:{key}";

    private static string AliasOf(string settingKey)
        => settingKey.EndsWith(":TranscriptionModel", StringComparison.Ordinal)
            ? "TranscriptionDeployment"
            : "Deployment";

    private static string Host(string? endpoint)
        => !string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            ? uri.Host
            : "端點未設定";

    private static string Truncate(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body.Length <= MaxBodyLength ? body : body[..MaxBodyLength] + "…";
    }
}
