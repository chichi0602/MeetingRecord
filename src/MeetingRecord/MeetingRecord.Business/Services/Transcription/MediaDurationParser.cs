using System.Globalization;
using System.Text.RegularExpressions;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 從 FFmpeg 的輸出解析音訊時長。抽成純函式以便單元測試——與 <see cref="TranscriptChunker"/>、
/// <see cref="TranscriptionNoiseFilter"/> 同一個慣例，不碰 IO、不碰行程。
///
/// <para>
/// 時長是**語音轉錄的計費單位**（Azure 按分鐘計價），所以這裡解析失敗不是小事：
/// 整場會議的轉錄費用會變成零，而畫面上看起來一切正常。
/// </para>
/// </summary>
public static partial class MediaDurationParser
{
    /// <summary>
    /// ⚠️ 只押到**第一個逗號**為止，刻意不寫成 <c>Duration: (.+?), start:</c>。
    ///
    /// <para>
    /// 實測（FFmpeg 9.0.1）：<c>Duration: 00:00:05.57, bitrate: 705 kb/s</c>
    /// ——<b>沒有 start: 欄位</b>。<c>start:</c> 只在容器帶非零起始時間戳時才出現，
    /// 押著它會讓所有一般檔案都解析失敗，然後靜靜地走進推估分支。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2})(?:\.(\d+))?\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    /// <summary>
    /// 從 FFmpeg 的 stderr 取出輸入檔的總時長。
    ///
    /// <para>
    /// ⚠️ 前提是 FFmpeg 以 <c>-loglevel info</c> 執行。<c>Duration:</c> 是 AV_LOG_INFO 等級，
    /// 用 <c>-loglevel error</c>（0.4.79 之前的設定）跑，成功時 stderr <b>完全是空的</b>，
    /// 這裡永遠回 null。見 <see cref="FfmpegMediaConverter.BuildSegmentArguments"/>。
    /// </para>
    /// </summary>
    /// <returns>解析得到的時長；沒有可用的 <c>Duration:</c> 行（含 <c>Duration: N/A</c>）時回 null。</returns>
    public static TimeSpan? TryParseDuration(string? ffmpegOutput)
    {
        if (string.IsNullOrWhiteSpace(ffmpegOutput))
        {
            return null;
        }

        var match = DurationPattern().Match(ffmpegOutput);
        if (!match.Success)
        {
            // Duration: N/A 會走到這裡。回 null 而不是 0——0 會讓整場會議的轉錄費用變成免費。
            return null;
        }

        var hours = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);

        // 小數位數不固定（常見兩位，但不保證），一律補成毫秒再取。
        var milliseconds = 0;
        if (match.Groups[4].Success)
        {
            var fraction = match.Groups[4].Value.PadRight(3, '0')[..3];
            milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture);
        }

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    /// <summary>
    /// 把總時長拆成每一段實際的秒數。
    ///
    /// <para>
    /// 轉錄是逐段呼叫的（每段 <see cref="FfmpegMediaConverter.SegmentSeconds"/> 秒），
    /// 帳本要一段記一筆，所以最後一段不能也算成整段——那會把費用高估。
    /// </para>
    /// </summary>
    /// <param name="totalDuration">整個音檔的時長；null 時回傳全部為 null 的清單長度 0。</param>
    /// <param name="segmentCount">實際切出來的段數。</param>
    /// <param name="segmentSeconds">每段的目標秒數。</param>
    public static IReadOnlyList<double> SplitSegmentSeconds(
        TimeSpan? totalDuration,
        int segmentCount,
        int segmentSeconds)
    {
        if (totalDuration is not { } duration || segmentCount <= 0 || segmentSeconds <= 0)
        {
            return [];
        }

        var remaining = duration.TotalSeconds;
        var result = new List<double>(segmentCount);

        for (var index = 0; index < segmentCount; index++)
        {
            // 最後一段拿剩下的；中間每段拿滿。負數夾成 0——切段數與時長對不上時
            // （例如時長是推估來的）不該產生負秒數。
            var isLast = index == segmentCount - 1;
            var taken = isLast ? Math.Max(remaining, 0) : Math.Min(segmentSeconds, Math.Max(remaining, 0));

            result.Add(taken);
            remaining -= taken;
        }

        return result;
    }

    /// <summary>
    /// FFmpeg 回報 <c>Duration: N/A</c> 時的退路：以 mp3 的位元組數回推秒數。
    ///
    /// <para>
    /// 轉出來的 mp3 是固定位元率（<c>-b:a 32k</c>，見
    /// <see cref="FfmpegMediaConverter.BuildSegmentArguments"/>），所以這個推估相當準；
    /// 但仍然是推估，帳本要標記起來，不要讓它看起來跟實測值一樣可信。
    /// </para>
    /// </summary>
    /// <param name="totalBytes">所有分段 mp3 的位元組總和。</param>
    /// <param name="bitsPerSecond">編碼位元率，預設 32kbps。</param>
    public static double? EstimateSecondsFromBytes(long totalBytes, int bitsPerSecond = 32_000)
    {
        if (totalBytes <= 0 || bitsPerSecond <= 0)
        {
            return null;
        }

        return totalBytes * 8d / bitsPerSecond;
    }
}
