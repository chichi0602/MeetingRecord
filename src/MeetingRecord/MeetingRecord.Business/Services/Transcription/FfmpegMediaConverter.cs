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

            await RunFfmpegAsync(arguments, cancellationToken);

            var segments = Directory
                .GetFiles(workingDirectory, "part_*.mp3")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (segments.Count == 0)
            {
                throw new InvalidOperationException(
                    "FFmpeg 未產生任何音訊分段，請確認來源檔是否含有可用的音軌。");
            }

            logger.LogInformation(
                "Converted media to mp3 segments. Source={Source}, SegmentCount={SegmentCount}",
                sourceFullPath,
                segments.Count);

            return new MediaSegmentSet(workingDirectory, segments);
        }
        catch
        {
            TryDeleteDirectory(workingDirectory);
            throw;
        }
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
            "-loglevel", "error",
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

    private async Task RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg 轉檔失敗（結束代碼 {process.ExitCode}）：{standardError.ToString().Trim()}");
        }
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
