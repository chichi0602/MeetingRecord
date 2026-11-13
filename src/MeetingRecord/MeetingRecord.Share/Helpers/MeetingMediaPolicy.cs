namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 會議影音檔的允收政策（前端與服務層共用同一份判斷，避免兩邊漂移）。
///
/// 上傳端接受常見的音訊與視訊格式；送交語音轉錄前一律由 FFmpeg 轉成 mp3 並切段，
/// 所以這份白名單只需要涵蓋「FFmpeg 讀得懂」的格式，不必受轉錄 API 的格式限制約束。
///
/// 1.1.23 從 Business 搬到 Share：WASM 版會議頁也要在畫面先擋一次。
/// </summary>
public static class MeetingMediaPolicy
{
    /// <summary>
    /// 轉錄時每段的長度（秒）。FFmpeg 依它切段（<c>FfmpegMediaConverter.SegmentSeconds</c>），
    /// 畫面的費用說明也用它算分鐘數，兩邊同一個來源，改常數時文案不會偷偷過期。
    /// ⚠️ 真正的限制不是檔案大小，而是轉錄模型的<b>輸出長度</b>：gpt-4o-transcribe 單次最多輸出約 2000 token
    /// （中文約 2000 多字），超過的部分會被靜默截掉、API 不回錯誤。以前每段 15 分鐘，
    /// 講話密集時每段都停在句子中間、後半段整個不見；改成 5 分鐘（約 1500 字）才留得出餘裕。不要再拉長。
    /// </summary>
    public const int TranscriptionSegmentSeconds = 300;

    /// <summary>單一影音檔上限：1GB。</summary>
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    /// <summary>允許上傳的副檔名（一律小寫，含前置點）。</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions =
    [
        ".mp3", ".wma", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".amr",
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm",
    ];

    /// <summary>供 UI 提示與錯誤訊息使用的格式清單文字。</summary>
    public static string AllowedExtensionsText => string.Join("、", AllowedExtensions);

    /// <summary>供 input[type=file] 的 accept 屬性使用。</summary>
    public static string AcceptAttribute => string.Join(",", AllowedExtensions);

    /// <summary>檔名的副檔名是否在白名單內。</summary>
    public static bool IsAllowedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return AllowedExtensions.Contains(extension.ToLowerInvariant());
    }

    /// <summary>把位元組數格式化為易讀字串（清單與提示訊息共用）。</summary>
    public static string FormatFileSize(long fileSize)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = fileSize;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
