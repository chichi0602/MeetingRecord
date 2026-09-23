using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 一次文字生成的結果。
///
/// <para>
/// <paramref name="Usage"/> 為 null 代表**供應商沒有回報用量**——串流被中斷、或
/// api-version 不支援 <c>stream_options</c> 都會這樣。那不是錯誤，帳本會把 token 欄位留白
/// 而不是記成 0（0 會讓人以為那次呼叫不用錢）。
/// </para>
/// </summary>
public sealed record TextGenerationResult(string Content, AiTokenUsage? Usage);

/// <summary>
/// 文字生成（chat completion）供應商。這是「未來支援其他廠商」的擴充點：
/// 新增一個實作並在 <c>LlmSettings.DefaultProvider</c> 指定其 <see cref="ProviderName"/> 即可切換。
///
/// <para>
/// 0.4.80 起回傳 <see cref="TextGenerationResult"/> 而非純字串，以便把 token 用量帶給用量帳本。
/// ⚠️ 刻意**不保留** <c>Task&lt;string&gt;</c> 的多載：兩個並存時，新程式碼會隨手挑到丟棄用量的那一個，
/// 而帳本要求涵蓋**全部**付費呼叫。這裡的編譯錯誤是資產。
/// </para>
/// </summary>
public interface ITextGenerationProvider
{
    /// <summary>供應商名稱，需與 <c>LlmSettings:Providers</c> 的鍵一致（比對不分大小寫）。</summary>
    string ProviderName { get; }

    /// <summary>
    /// 實際使用的 deployment／model 名稱。
    /// **單價是以這個字串查表的**，失敗時呼叫端也要靠它才知道這次打在哪個模型上。
    /// </summary>
    string ModelName { get; }

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
    /// <param name="images">
    /// 隨使用者訊息一起送的圖片（0.4.95，AI 問答附件）。null 或空＝純文字請求，與之前完全相同。
    /// ⚠️ 需要支援影像輸入的模型；不支援的 deployment 會由供應商回 400，錯誤照常往上拋。
    /// </param>
    Task<TextGenerationResult> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onDelta,
        CancellationToken cancellationToken,
        IReadOnlyList<PromptImage>? images = null);
}

/// <summary>送進模型的一張圖片。</summary>
/// <param name="MediaType">MIME 類型，例如 <c>image/png</c>。</param>
/// <param name="Content">圖片的原始位元組。</param>
public sealed record PromptImage(string MediaType, byte[] Content);
