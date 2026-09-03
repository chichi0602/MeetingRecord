using System.Text;
using System.Net;
using Markdig;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 把 AI 產生的會議紀錄組成可列印成 PDF 的 HTML 文件。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker.Split</c>、
/// <c>AzureOpenAiTextGenerationProvider.BuildRequestUri</c> 同一個慣例。
///
/// <c>Meeting.DraftContent</c> 本身就是 Markdown（LLM 依提示詞範本產出 <c>##</c> 標題與條列），
/// 由 Markdig 轉成 HTML 後交給無頭瀏覽器列印。本類別只產生 HTML 與安全檔名，
/// <b>不碰任何 IO</b>——實際跑瀏覽器的是 <see cref="HeadlessBrowserPdfRenderer"/>。
/// </summary>
public static class MeetingDocumentExporter
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
    /// Markdig 管線。<b>必須啟用 advanced extensions</b>——提示詞範本由使用者自行撰寫，
    /// 系統沒有規範 LLM 的輸出格式，實務上很常出現表格（決議／負責人／期限）。
    /// 少了這個，表格會原封不動印成 <c>|---|---|</c>。
    /// </summary>
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// 組出要交給無頭瀏覽器列印的完整 HTML：一段表頭（專案、會議、日期、提示詞、產生時間），
    /// 接著把會議紀錄內文（Markdown）轉成 HTML。
    /// </summary>
    /// <param name="meeting">要匯出的會議紀錄，需已產生草稿。</param>
    /// <param name="projectTitle">所屬專案名稱；傳 null 時取 <paramref name="meeting"/> 上的值。</param>
    public static string BuildHtml(MeetingAdapterModel meeting, string? projectTitle = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        var title = Fallback(meeting.Title, UnspecifiedText);
        var project = Fallback(projectTitle ?? meeting.ProjectTitle, UnspecifiedText);
        var body = Markdown.ToHtml(meeting.DraftContent ?? string.Empty, MarkdownPipeline);

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-Hant\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(title)}</title>");
        builder.AppendLine($"<style>{PrintStyle}</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine($"<h1 class=\"doc-title\">會議紀錄：{Escape(title)}</h1>");
        builder.AppendLine("<dl class=\"doc-meta\">");
        AppendMeta(builder, "專案", project);
        AppendMeta(builder, "會議", title);
        AppendMeta(builder, "會議日期", FormatDate(meeting.MeetingDate));
        AppendMeta(builder, "使用提示詞", Fallback(meeting.DraftPromptTemplateName, UnspecifiedText));
        AppendMeta(builder, "產生時間", FormatDateTime(meeting.DraftCompletedAt));
        builder.AppendLine("</dl>");
        builder.AppendLine("<hr class=\"doc-rule\">");
        builder.AppendLine("<main class=\"doc-body\">");
        builder.AppendLine(body.TrimEnd());
        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static void AppendMeta(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"<div class=\"doc-meta-row\"><dt>{Escape(label)}</dt><dd>{Escape(value)}</dd></div>");
    }

    /// <summary>
    /// 表頭欄位是使用者輸入（會議標題常常就是上傳的檔名），必須逸出。
    /// 內文不經過這裡——它走 Markdig，已由該套件處理。
    /// </summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    /// <summary>
    /// 列印樣式。字型堆疊把 Windows 與 macOS 的常見中文字型都列上，
    /// 交給瀏覽器挑——這正是不必把 CJK 字型檔嵌進專案的原因。
    /// </summary>
    private const string PrintStyle = """
        @page { size: A4; margin: 18mm 16mm; }
        body {
            margin: 0;
            font-family: "Microsoft JhengHei", "PingFang TC", "Noto Sans TC", "Hiragino Sans", sans-serif;
            font-size: 11pt;
            line-height: 1.7;
            color: #1f2d3d;
        }
        .doc-title { font-size: 18pt; margin: 0 0 12px; }
        .doc-meta { margin: 0; font-size: 10pt; color: #4a5a6b; }
        .doc-meta-row { display: flex; gap: 8px; margin: 2px 0; }
        .doc-meta dt { flex: none; min-width: 5em; font-weight: 600; }
        .doc-meta dd { margin: 0; }
        .doc-rule { margin: 14px 0 18px; border: none; border-top: 1px solid #c8d1da; }
        .doc-body h1 { font-size: 16pt; }
        .doc-body h2 { font-size: 14pt; margin: 18px 0 8px; }
        .doc-body h3 { font-size: 12pt; margin: 14px 0 6px; }
        .doc-body p { margin: 8px 0; }
        .doc-body ul, .doc-body ol { margin: 8px 0; padding-left: 1.6em; }
        .doc-body li { margin: 3px 0; }
        .doc-body pre {
            background: #f5f7fa;
            padding: 10px 12px;
            border-radius: 4px;
            white-space: pre-wrap;
            word-break: break-word;
        }
        .doc-body code { font-family: Consolas, "Courier New", monospace; font-size: 10pt; }
        .doc-body blockquote {
            margin: 8px 0;
            padding-left: 12px;
            border-left: 3px solid #c8d1da;
            color: #4a5a6b;
        }
        .doc-body table {
            width: 100%;
            border-collapse: collapse;
            margin: 10px 0;
            font-size: 10pt;
            table-layout: fixed;
        }
        .doc-body th, .doc-body td {
            border: 1px solid #c8d1da;
            padding: 5px 8px;
            text-align: left;
            vertical-align: top;
            word-break: break-word;
        }
        .doc-body th { background: #f0f3f7; font-weight: 600; }
        /* 標題不要落在頁尾、表格列不要被切成兩半。 */
        .doc-body h1, .doc-body h2, .doc-body h3 { break-after: avoid; }
        .doc-body tr, .doc-body li { break-inside: avoid; }
        """;

    /// <summary>
    /// 產生安全的下載檔名：<c>會議紀錄_{標題}_{產生日}.pdf</c>。
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

        return $"會議紀錄_{title}_{stamp}.pdf";
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
