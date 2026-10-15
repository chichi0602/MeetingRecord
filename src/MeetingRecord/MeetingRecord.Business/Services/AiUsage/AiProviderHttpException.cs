namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// AI 供應商回了非 2xx（1.0.29）。繼承 <see cref="InvalidOperationException"/>、訊息不變，
/// 既有的 catch 與畫面行為都不受影響；多帶 <see cref="StatusCode"/> 讓 AI 對話紀錄記下 HTTP 狀態。
/// </summary>
public sealed class AiProviderHttpException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}
