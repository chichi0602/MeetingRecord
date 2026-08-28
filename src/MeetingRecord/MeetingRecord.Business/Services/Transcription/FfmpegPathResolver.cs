using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 判斷設定的 FFmpeg 執行檔是不是真的啟得起來。
///
/// <para>
/// <see cref="MediaSettings.FfmpegPath"/> 允許填完整路徑，也允許只填 <c>ffmpeg</c> 走 PATH，
/// 所以不能只做 <see cref="File.Exists(string)"/>——那會把正確的 PATH 設定誤判成不存在。
/// 這裡的解析方式對齊 <c>Process.Start</c>（UseShellExecute = false）的行為，
/// 讓「檢查通過」等同於「真的啟得起來」。
/// </para>
/// </summary>
public static class FfmpegPathResolver
{
    /// <summary>設定值是否指向一個實際存在的執行檔（完整路徑或 PATH 上的檔名皆可）。</summary>
    public static bool Exists(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return false;
        }

        var path = ffmpegPath.Trim();

        // 帶了目錄資訊就當成路徑直接檢查，不再往 PATH 找。
        if (Path.IsPathRooted(path)
            || path.Contains(Path.DirectorySeparatorChar)
            || path.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(path);
        }

        return ExistsOnSearchPath(path);
    }

    private static bool ExistsOnSearchPath(string fileName)
    {
        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return false;
        }

        var extensions = GetExecutableExtensions(fileName);
        var directories = searchPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                if (File.Exists(Path.Combine(directory, fileName + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 要嘗試的副檔名。已自帶副檔名（如 <c>ffmpeg.exe</c>）就只試原名，
    /// 否則沿 PATHEXT 逐一補上，與命令列的解析順序一致。
    /// </summary>
    private static string[] GetExecutableExtensions(string fileName)
    {
        if (Path.HasExtension(fileName))
        {
            return [string.Empty];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return [string.Empty, ".exe"];
        }

        return
        [
            string.Empty,
            .. pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        ];
    }
}
