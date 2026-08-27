namespace MeetingRecord.Models.Systems;

/// <summary>
/// 影音處理設定。語音轉錄前一律以 FFmpeg 將上傳的影音檔轉成 mp3 並切段，
/// 因此 FFmpeg 執行檔位置必須可由設定調整（不同機器的安裝路徑不同）。
/// </summary>
public class MediaSettings
{
    public const string SectionName = "MediaSettings";

    /// <summary>FFmpeg 執行檔完整路徑；若已加入 PATH 也可以只填 ffmpeg。</summary>
    public string FfmpegPath { get; set; } = string.Empty;
}
