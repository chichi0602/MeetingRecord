namespace MeetingRecord.Dtos.Diagnostics;

/// <summary>「測試 AI 連線」一個探測的結果（1.0.35；1.1.35 從 Business 搬來，系統健康 API 與 WASM 頁共用）。</summary>
/// <param name="Item">「文字生成」或「語音轉錄」。</param>
/// <param name="Success">部署存在而且呼叫成功。</param>
/// <param name="Deployment">實際送出的部署名稱；未設定時為空字串。</param>
/// <param name="Host">端點主機；未設定時為空字串。</param>
/// <param name="Elapsed">從送出到收到回應的時間；沒有送出時為 null。</param>
/// <param name="Message">給管理者看的結論或失敗原因。</param>
public sealed record AiConnectionProbeResult(
    string Item,
    bool Success,
    string Deployment,
    string Host,
    TimeSpan? Elapsed,
    string Message);
