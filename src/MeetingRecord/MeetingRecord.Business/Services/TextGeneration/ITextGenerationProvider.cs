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

    /// <summary>
    /// 產生文字。
    /// </summary>
    /// <param name="onCharactersGenerated">
    /// 生成過程中回報「目前累計已產生的字元數」，供進度畫面使用；不需要進度時傳 null。
    ///
    /// <para>
    /// 刻意用 <see cref="Action{T}"/> 而不是 <c>IProgress&lt;int&gt;</c>：
    /// <c>Progress&lt;T&gt;</c> 會捕捉 SynchronizationContext 改用非同步派送，
    /// 但這裡的呼叫端在背景執行緒、接收端（通知器）本身就是 thread-safe，
    /// 同步呼叫即可，不必多繞一層。
    /// </para>
    /// </param>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<int>? onCharactersGenerated,
        CancellationToken cancellationToken);
}
