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

    /// <summary>
    /// 1.1.73：指定每段的長度上限（秒）。LLM Speech 一次送整個檔案，上限是設定的 <c>MaxFileSeconds</c>（預設 2 小時）。
    /// 預設實作忽略上限、照固定長度切——測試的假轉檔器不用改。
    /// </summary>
    Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, int maxSegmentSeconds, CancellationToken cancellationToken)
        => ConvertToMp3SegmentsAsync(sourceFullPath, cancellationToken);
}

/// <summary>
/// 1.1.72 轉錄比較：剪出一段和正式轉錄同規格的 mp3（16kHz 單聲道 32kbps）。
/// 另開介面的理由同 <see cref="IMediaSegmentSplitter"/>：測試替換 <see cref="IMediaConverter"/> 時不受影響。
/// </summary>
public interface IMediaExcerptCutter
{
    /// <summary>剪不出來（來源不存在、起點超過結尾）時回 null。</summary>
    Task<MediaExcerpt?> CutMp3ExcerptAsync(string sourceFullPath, double startSeconds, double seconds, CancellationToken cancellationToken);
}

/// <summary>剪出來的片段。<paramref name="Seconds"/> 是實際長度（來源不夠長時比要求的短），轉錄按它計費。</summary>
public sealed record MediaExcerpt(byte[] Mp3, double Seconds);

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
    /// <param name="segmentSeconds">每一段實際的秒數（1.1.61 起切在停頓處，各段不一樣長）；null 表示各段等長。</param>
    public MediaSegmentSet(
        string workingDirectory,
        IReadOnlyList<string> segmentFullPaths,
        TimeSpan? totalDuration = null,
        bool isDurationEstimated = false,
        IReadOnlyList<double>? segmentSeconds = null)
    {
        this.workingDirectory = workingDirectory;
        SegmentFullPaths = segmentFullPaths;
        TotalDuration = totalDuration;
        IsDurationEstimated = isDurationEstimated;
        SegmentSeconds = segmentSeconds;
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

    /// <summary>
    /// 每一段實際的秒數（轉錄按時長計費，一段記一筆）。null 時呼叫端以「各段等長、最後一段拿剩下的」推算。
    /// </summary>
    public IReadOnlyList<double>? SegmentSeconds { get; }

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
