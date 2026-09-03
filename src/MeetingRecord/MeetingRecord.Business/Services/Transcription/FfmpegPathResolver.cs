using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 判斷設定的 FFmpeg 執行檔是不是真的啟得起來。
///
/// <para>
/// <see cref="MediaSettings.FfmpegPath"/> 允許填完整路徑，也允許只填 <c>ffmpeg</c> 走 PATH。
/// 實際解析邏輯與其他外部執行檔共用，見 <see cref="ExecutablePathResolver"/>。
/// </para>
/// </summary>
public static class FfmpegPathResolver
{
    /// <summary>設定值是否指向一個實際存在的執行檔（完整路徑或 PATH 上的檔名皆可）。</summary>
    public static bool Exists(string? ffmpegPath) => ExecutablePathResolver.Exists(ffmpegPath);
}
