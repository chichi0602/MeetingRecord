using System.Text;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 把 AI 產生的會議紀錄組成可下載的 Markdown 文件。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker.Split</c>、
/// <c>AzureOpenAiTextGenerationProvider.BuildRequestUri</c> 同一個慣例。
///
/// <c>Meeting.DraftContent</c> 本身就是 Markdown（LLM 依提示詞範本產出 <c>##</c> 標題與條列），
/// 因此內文<b>原樣輸出、不做任何轉換</b>；本類別只負責加上表頭與產生安全檔名。
/// </summary>
public static class MeetingMarkdownExporter
{
    /// <summary>欄位無值時的顯示文字，與畫面上的表達一致。</summary>
    public const string UnspecifiedText = "未指定";

    /// <summary>檔名中「標題」部分的字元上限，避免整體路徑超過 Windows 的長度限制。</summary>
    public const int MaxTitleLengthInFileName = 60;

    /// <summary>標題若本身是上傳的影音檔名，匯出時把副檔名去掉會比較像文件名稱。</summary>
    private static readonly string[] MediaExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus",
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".m4v",
    ];

    /// <summary>
    /// 組出完整的 Markdown 文件：一段表頭（專案、會議、日期、提示詞、產生時間）
    /// 加上分隔線，再接會議紀錄內文原文。
    /// </summary>
    /// <param name="meeting">要匯出的會議紀錄，需已產生草稿。</param>
    /// <param name="projectTitle">所屬專案名稱；傳 null 時取 <paramref name="meeting"/> 上的值。</param>
    public static string BuildDocument(MeetingAdapterModel meeting, string? projectTitle = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        var title = Fallback(meeting.Title, UnspecifiedText);
        var project = Fallback(projectTitle ?? meeting.ProjectTitle, UnspecifiedText);

        var builder = new StringBuilder();
        builder.AppendLine($"# 會議紀錄：{title}");
        builder.AppendLine();
        builder.AppendLine($"- 專案：{project}");
        builder.AppendLine($"- 會議：{title}");
        builder.AppendLine($"- 會議日期：{FormatDate(meeting.MeetingDate)}");
        builder.AppendLine($"- 使用提示詞：{Fallback(meeting.DraftPromptTemplateName, UnspecifiedText)}");
        builder.AppendLine($"- 產生時間：{FormatDateTime(meeting.DraftCompletedAt)}");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine((meeting.DraftContent ?? string.Empty).TrimEnd());

        return builder.ToString();
    }

    /// <summary>
    /// 產生安全的下載檔名：<c>會議紀錄_{標題}_{產生日}.md</c>。
    /// 標題會去掉影音副檔名、濾除檔名非法字元、壓縮連續空白並截斷過長內容。
    /// </summary>
    public static string BuildFileName(MeetingAdapterModel meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        var title = Sanitize(StripMediaExtension(meeting.Title));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = UnspecifiedText;
        }

        if (title.Length > MaxTitleLengthInFileName)
        {
            title = title[..MaxTitleLengthInFileName].TrimEnd();
        }

        // 草稿完成才有匯出鈕，DraftCompletedAt 幾乎一定有值；沒有就退回最後更新時間。
        var stamp = (meeting.DraftCompletedAt ?? meeting.UpdatedAt).ToString("yyyyMMdd");

        return $"會議紀錄_{title}_{stamp}.md";
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

    private static string FormatDate(DateTime? value)
        => value?.ToString("yyyy/MM/dd") ?? UnspecifiedText;

    private static string FormatDateTime(DateTime? value)
        => value?.ToString("yyyy/MM/dd HH:mm") ?? UnspecifiedText;

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
