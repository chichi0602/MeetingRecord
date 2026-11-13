namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 把一個已切好的 mp3 分段再對半切（1.1.61）。轉錄結果疑似被輸出上限截斷時，
/// <see cref="TranscriptionJobRunner"/> 用它把該段拆成兩半重轉。
///
/// <para>
/// 刻意不併進 <see cref="IMediaConverter"/>：端對端測試的假轉檔器只實作那個介面，
/// 分開之後它不必跟著改；沒有註冊這個服務時，轉錄照常進行、只是少了這道保險。
/// </para>
/// </summary>
public interface IMediaSegmentSplitter
{
    /// <summary>
    /// 在中間附近的停頓把分段切成兩半，檔案放在原分段的同一個目錄（隨 <see cref="MediaSegmentSet"/> 一起清掉）。
    /// </summary>
    /// <returns>前後兩半的完整路徑與秒數；切不出兩段時回 null。</returns>
    Task<IReadOnlyList<(string FullPath, double Seconds)>?> SplitInHalfAsync(string segmentFullPath, CancellationToken cancellationToken);
}
