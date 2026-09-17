using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 以外部 FFmpeg 執行檔把影音檔轉為 16kHz 單聲道 32kbps 的 mp3，並固定切成多個分段。
///
/// <para>
/// 為什麼「一律切段」：語音轉錄 API 有單檔大小上限（Azure OpenAI 為 25MB），
/// 而上傳端允許到 1GB。固定切段讓長會議與短錄音走同一條路徑，
/// 不必為「檔案夠小就不切」多維護一個分支。
/// 15 分鐘 @ 32kbps 約 3.6MB，遠低於上限。
/// </para>
/// </summary>
public class FfmpegMediaConverter : IMediaConverter
{
    /// <summary>每個分段的長度（秒）。</summary>
    public const int SegmentSeconds = 900;

    /// <summary>分段檔名樣板（FFmpeg segment muxer 使用）。</summary>
    public const string SegmentFileNamePattern = "part_%04d.mp3";

    /// <summary>FFmpeg 單次轉檔的逾時上限。</summary>
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(30);

    private readonly string ffmpegPath;
    private readonly ILogger<FfmpegMediaConverter> logger;

    public FfmpegMediaConverter(IOptions<MediaSettings> mediaSettings, ILogger<FfmpegMediaConverter> logger)
    {
        ffmpegPath = mediaSettings.Value.FfmpegPath;
        this.logger = logger;
    }

    public async Task<MediaSegmentSet> ConvertToMp3SegmentsAsync(string sourceFullPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new InvalidOperationException(
                $"{MediaSettings.SectionName}:FfmpegPath 未設定，無法將影音檔轉為 mp3。");
        }

        if (!File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException($"找不到要轉檔的影音檔：{sourceFullPath}", sourceFullPath);
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "MeetingRecord", "transcode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var outputPattern = Path.Combine(workingDirectory, SegmentFileNamePattern);
            var arguments = BuildSegmentArguments(sourceFullPath, outputPattern, SegmentSeconds);

            var ffmpegOutput = await RunFfmpegAsync(arguments, cancellationToken);

            var segments = Directory
                .GetFiles(workingDirectory, "part_*.mp3")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (segments.Count == 0)
            {
                throw new InvalidOperationException(
                    "FFmpeg 未產生任何音訊分段，請確認來源檔是否含有可用的音軌。");
            }

            var (totalDuration, isEstimated) = ResolveDuration(ffmpegOutput, segments);

            logger.LogInformation(
                "Converted media to mp3 segments. Source={Source}, SegmentCount={SegmentCount}, " +
                "TotalSeconds={TotalSeconds}, DurationEstimated={DurationEstimated}",
                sourceFullPath,
                segments.Count,
                totalDuration?.TotalSeconds,
                isEstimated);

            return new MediaSegmentSet(workingDirectory, segments, totalDuration, isEstimated);
        }
        catch
        {
            TryDeleteDirectory(workingDirectory);
            throw;
        }
    }

    /// <summary>
    /// 決定這個音檔的總時長，並標示它是實測還是推估。
    ///
    /// <para>
    /// 優先用 FFmpeg 回報的 <c>Duration:</c>；回報 <c>N/A</c> 時退回以 mp3 位元組推估
    /// （轉出來的是固定 32kbps，推估相當準，但仍要標記起來，帳本上才分得出來）。
    /// 兩條路都走不通就回 null——**寧可沒有數字，也不要一個看起來像真的的零**。
    /// </para>
    /// </summary>
    private (TimeSpan? Duration, bool IsEstimated) ResolveDuration(
        string ffmpegOutput,
        IReadOnlyList<string> segments)
    {
        if (MediaDurationParser.TryParseDuration(ffmpegOutput) is { } parsed)
        {
            return (parsed, false);
        }

        long totalBytes = 0;
        foreach (var segment in segments)
        {
            try
            {
                totalBytes += new FileInfo(segment).Length;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to measure segment size. FullPath={FullPath}", segment);
            }
        }

        var estimated = MediaDurationParser.EstimateSecondsFromBytes(totalBytes);
        if (estimated is not { } seconds)
        {
            logger.LogWarning("Could not determine media duration; usage will be recorded without it.");
            return (null, false);
        }

        return (TimeSpan.FromSeconds(seconds), true);
    }

    /// <summary>
    /// 組出 FFmpeg 的命令列參數。抽成純函式以便單元測試。
    /// </summary>
    internal static string BuildSegmentArguments(string sourceFullPath, string outputPattern, int segmentSeconds)
    {
        // -vn 丟棄視訊軌（mp4 等影片只取聲音）；-ac 1 -ar 16000 -b:a 32k 壓到語音辨識足夠的品質，
        // 讓每個分段都遠小於轉錄 API 的單檔上限。
        return string.Join(' ',
            "-hide_banner",
            "-nostdin",
            // ⚠️ 必須是 info 而不是 error。音檔時長是語音轉錄的計費單位，而它來自 stderr 的
            // 「Duration:」那一行——那是 AV_LOG_INFO 等級，用 error 跑的話成功時 stderr
            // 完全是空的（實測 FFmpeg 9.0.1），時長永遠拿不到、轉錄費用會整片變成零。
            // -nostats 同樣不可省：不加的話每 0.5 秒會吐一行 \r 結尾的進度行，三小時的檔
            // 會累積上百行，而這些字串在失敗時會被塞進 Meeting.TranscriptionError 顯示在畫面上。
            "-loglevel", "info",
            "-nostats",
            "-y",
            "-i", Quote(sourceFullPath),
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "libmp3lame",
            "-b:a", "32k",
            "-f", "segment",
            "-segment_time", segmentSeconds.ToString(),
            "-segment_format", "mp3",
            "-reset_timestamps", "1",
            Quote(outputPattern));
    }

    private static string Quote(string value) => $"\"{value}\"";

    /// <summary>
    /// 執行 FFmpeg，回傳它寫到 stderr 的全部內容。
    ///
    /// <para>
    /// 回傳值不只是為了錯誤訊息——<c>Duration:</c> 那一行就在裡面，是音檔時長的唯一來源。
    /// </para>
    /// </summary>
    private async Task<string> RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"無法啟動 FFmpeg（{MediaSettings.SectionName}:FfmpegPath = {ffmpegPath}），請確認路徑正確且檔案存在。", ex);
        }

        var standardError = new StringBuilder();
        var errorTask = ReadAllAsync(process.StandardError, standardError, cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ConversionTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await Task.WhenAll(errorTask, outputTask);

        var output = standardError.ToString();

        if (process.ExitCode != 0)
        {
            // ⚠️ 只取尾端：0.4.80 起 loglevel 是 info，輸出會多出輸入檔的分析區塊，
            // 而這個訊息會被寫進 Meeting.TranscriptionError 直接顯示在畫面上。
            // 真正的錯誤原因一律在最後面。
            throw new InvalidOperationException(
                $"FFmpeg 轉檔失敗（結束代碼 {process.ExitCode}）：{TakeTail(output, 500)}");
        }

        return output;
    }

    /// <summary>取字串尾端最多 <paramref name="maxLength"/> 個字，前面補上刪節號。</summary>
    private static string TakeTail(string value, int maxLength)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : "…" + trimmed[^maxLength..];
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder target, CancellationToken cancellationToken)
    {
        var text = await reader.ReadToEndAsync(cancellationToken);
        target.Append(text);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
