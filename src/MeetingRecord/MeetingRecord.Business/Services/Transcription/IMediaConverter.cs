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

    public MediaSegmentSet(string workingDirectory, IReadOnlyList<string> segmentFullPaths)
    {
        this.workingDirectory = workingDirectory;
        SegmentFullPaths = segmentFullPaths;
    }

    /// <summary>依序排列的 mp3 分段完整路徑。</summary>
    public IReadOnlyList<string> SegmentFullPaths { get; }

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
