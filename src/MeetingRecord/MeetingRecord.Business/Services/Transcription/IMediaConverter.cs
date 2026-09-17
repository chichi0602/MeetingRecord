namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 影音檔前處理：抽出音軌、轉成 mp3 並依時長切段。
/// </summary>
public interface IMediaConverter
{
    /// <summary>
    /// 將任意影音檔轉為多個 mp3 分段。回傳值需以 using 釋放，會一併刪除暫存目錄。
    /// </summary>
    Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken);
}

/// <summary>
/// 一次轉檔產生的 mp3 分段集合，Dispose 時刪除整個暫存工作目錄。
/// </summary>
public sealed class MediaSegmentSet : IDisposable
{
    private readonly string workingDirectory;

    /// <param name="totalDuration">
    /// 整個音檔的時長；取不到時為 null。**選擇性參數**是為了讓既有的建構呼叫不必全部改寫。
    /// </param>
    /// <param name="isDurationEstimated">時長是以 mp3 位元組推估而非 FFmpeg 實測。</param>
    public MediaSegmentSet(
        string workingDirectory,
        IReadOnlyList<string> segmentFullPaths,
        TimeSpan? totalDuration = null,
        bool isDurationEstimated = false)
    {
        this.workingDirectory = workingDirectory;
        SegmentFullPaths = segmentFullPaths;
        TotalDuration = totalDuration;
        IsDurationEstimated = isDurationEstimated;
    }

    /// <summary>依序排列的 mp3 分段完整路徑。</summary>
    public IReadOnlyList<string> SegmentFullPaths { get; }

    /// <summary>
    /// 整個音檔的時長。**這是語音轉錄的計費單位**（Azure 按分鐘計價），供用量帳本記錄。
    /// 取不到時為 null——帳本會把時長留白，而不是記成 0。
    /// </summary>
    public TimeSpan? TotalDuration { get; }

    /// <summary>
    /// 時長是推估來的（FFmpeg 回報 <c>Duration: N/A</c>，改以 mp3 位元組回推）。
    /// 帳本要標記起來，不要讓推估值看起來跟實測值一樣可信。
    /// </summary>
    public bool IsDurationEstimated { get; }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 暫存目錄清不掉不影響轉錄結果，交由作業系統的暫存清理處理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
