namespace MeetingRecord.Business.Services.Transcription;

/// <summary>一位已知講者的聲音參考：送給 API 的名稱與 2～10 秒的 data URL（<c>data:audio/wav;base64,…</c>）。</summary>
public sealed record SpeakerReference(string Name, string DataUrl);

/// <summary>
/// 能分講者、而且能帶聲音參考讓整場代號一致的轉錄供應商（1.1.64）。
///
/// <para>
/// 刻意另開介面、不改 <see cref="ITranscriptionProvider"/>：測試與端對端的假供應商只實作原介面，
/// 不必跟著改；不支援的供應商照舊走每段各自標代號。
/// </para>
/// </summary>
public interface ISpeakerAwareTranscriptionProvider : ITranscriptionProvider
{
    /// <summary>目前的部署會不會分講者（例如部署名稱含 diarize）。</summary>
    bool SupportsSpeakers { get; }

    /// <summary>
    /// 轉錄一段音訊，回傳每句的講者與時間。<paramref name="references"/> 裡認得的講者，
    /// <c>speaker</c> 會是參考的 <see cref="SpeakerReference.Name"/>；認不得的照舊是字母。
    /// </summary>
    Task<DiarizedTranscription> TranscribeWithSpeakersAsync(
        Stream audio,
        string fileName,
        IReadOnlyList<SpeakerReference> references,
        CancellationToken cancellationToken);
}

/// <summary>
/// 從音檔剪一小段 wav 當講者的聲音參考（1.1.64）。
/// 理由同 <see cref="IMediaSegmentSplitter"/>：不併進 <see cref="IMediaConverter"/>，沒註冊時只是少了整場對齊。
/// </summary>
public interface IAudioClipCutter
{
    /// <summary>
    /// 剪出 16kHz 單聲道的 wav，放在來源檔的同一個目錄（隨 <see cref="MediaSegmentSet"/> 一起清掉）。
    /// </summary>
    /// <returns>wav 的完整路徑；剪不出來時回 null。</returns>
    Task<string?> CutWavClipAsync(string sourceFullPath, double startSeconds, double seconds, CancellationToken cancellationToken);
}
