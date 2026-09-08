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
    /// <param name="onDelta">
    /// 串流過程中逐段回報**新增的**文字（不是累積後的全文）；不需要即時內容時傳 null。
    ///
    /// <para>
    /// 傳增量而非累積字串，是因為累積會讓每個片段都配置一次完整長度的字串；
    /// 而且兩種呼叫端要的東西不同——會議紀錄生成只要「長度」（自己累加即可），
    /// AI 問答要的是「文字」以便邊生成邊顯示。傳增量兩者都拿得到。
    /// </para>
    ///
    /// <para>
    /// 刻意用 <see cref="Action{T}"/> 而不是 <c>IProgress&lt;T&gt;</c>：
    /// <c>Progress&lt;T&gt;</c> 會捕捉 SynchronizationContext 改用非同步派送，
    /// 但這裡的呼叫端在背景執行緒、接收端本身就是 thread-safe，同步呼叫即可。
    /// </para>
    /// </param>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onDelta,
        CancellationToken cancellationToken);
}
