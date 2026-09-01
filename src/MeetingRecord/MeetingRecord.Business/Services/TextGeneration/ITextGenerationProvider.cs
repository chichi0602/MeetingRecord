namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 文字生成（chat completion）供應商。這是「未來支援其他廠商」的擴充點：
/// 新增一個實作並在 <c>LlmSettings.DefaultProvider</c> 指定其 <see cref="ProviderName"/> 即可切換。
///
/// 與 <c>ITranscriptionProvider</c> 對稱：介面保持極簡，回傳純字串，
/// 不外露 token 用量等供應商細節（本系統刻意不做成本計量）。
/// </summary>
public interface ITextGenerationProvider
{
    /// <summary>供應商名稱，需與 <c>LlmSettings:Providers</c> 的鍵一致（比對不分大小寫）。</summary>
    string ProviderName { get; }

    Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
