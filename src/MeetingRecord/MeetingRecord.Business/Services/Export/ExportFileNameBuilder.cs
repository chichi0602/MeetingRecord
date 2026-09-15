using System.Text;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 匯出檔名的共用處理：去掉影音副檔名、濾除檔名非法字元、壓縮連續空白、截斷過長內容。
///
/// 抽成 internal static 純函式以便單元測試（本專案的既有慣例）。
/// </summary>
internal static class ExportFileNameBuilder
{
    /// <summary>檔名中「標題」部分的字元上限，避免整體路徑超過 Windows 的長度限制。</summary>
    public const int MaxTitleLength = 60;

    /// <summary>標題若本身是上傳的影音檔名，匯出時把副檔名去掉會比較像文件名稱。</summary>
    private static readonly string[] MediaExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus",
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".m4v",
    ];

    /// <summary>
    /// 把任意標題轉成可安全用於檔名的字串。什麼都不剩時回空字串，
    /// 要顯示什麼替代文字由呼叫端決定。
    /// </summary>
    public static string SafeTitle(string? title, int maxLength = MaxTitleLength)
    {
        var value = Sanitize(StripMediaExtension(title));

        return value.Length > maxLength
            ? value[..maxLength].TrimEnd()
            : value;
    }

    private static string StripMediaExtension(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        foreach (var extension in MediaExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^extension.Length];
            }
        }

        return value;
    }

    /// <summary>濾除檔名非法字元並把連續空白壓成單一底線。</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;

        foreach (var ch in value)
        {
            if (invalid.Contains(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                // 連續的空白或非法字元只留一個底線，避免產生「A___B」這種檔名。
                if (!lastWasSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSeparator = false;
        }

        return builder.ToString().Trim('_');
    }
}
