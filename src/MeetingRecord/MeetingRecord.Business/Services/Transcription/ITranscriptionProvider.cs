namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 語音轉文字（STT）供應商。這是「未來支援其他廠商」的擴充點：
/// 新增一個實作並在 <c>LlmSettings.TranscriptionProvider</c> 指定其 <see cref="ProviderName"/> 即可切換。
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>供應商名稱，需與 <c>LlmSettings:Providers</c> 的鍵一致（比對不分大小寫）。</summary>
    string ProviderName { get; }

    /// <summary>
    /// 將單一音訊段落轉成文字。
    /// </summary>
    /// <param name="audio">音訊內容（本專案一律傳入 mp3）。</param>
    /// <param name="fileName">送出時使用的檔名，供應商會用副檔名判斷格式。</param>
    Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken);
}
