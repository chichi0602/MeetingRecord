using System.Text;
using System.Net;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 把 AI 產生的會議紀錄組成可列印成 PDF 的 HTML 文件。
///
/// 抽成純函式以便單元測試——與 <c>TranscriptChunker.Split</c>、
/// <c>AzureOpenAiTextGenerationProvider.BuildRequestUri</c> 同一個慣例。
///
/// <c>Meeting.DraftContent</c> 本身就是 Markdown（LLM 依提示詞範本產出 <c>##</c> 標題與條列），
/// 由 <see cref="MarkdownRenderer"/> 轉成 HTML 後交給無頭瀏覽器列印。本類別只產生
/// HTML 與安全檔名，<b>不碰任何 IO</b>——實際跑瀏覽器的是 <see cref="HeadlessBrowserPdfRenderer"/>。
/// </summary>
public static class MeetingDocumentExporter
{
    /// <summary>欄位無值時的顯示文字，與畫面上的表達一致。</summary>
    public const string UnspecifiedText = "未指定";

    /// <summary>檔名中「標題」部分的字元上限，避免整體路徑超過 Windows 的長度限制。</summary>
    public const int MaxTitleLengthInFileName = ExportFileNameBuilder.MaxTitleLength;

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
        var body = MarkdownRenderer.ToHtml(meeting.DraftContent);

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-Hant\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(title)}</title>");
        builder.AppendLine($"<style>{ExportPrintStyles.Base}</style>");
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
    /// 內文不經過這裡——它走 <see cref="MarkdownRenderer"/>，已由該處理器負責。
    /// </summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    /// <summary>
    /// 產生安全的下載檔名：<c>會議紀錄_{標題}_{產生日}.pdf</c>。
    /// 標題會去掉影音副檔名、濾除檔名非法字元、壓縮連續空白並截斷過長內容。
    /// </summary>
    public static string BuildFileName(MeetingAdapterModel meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        var title = ExportFileNameBuilder.SafeTitle(meeting.Title, MaxTitleLengthInFileName);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = UnspecifiedText;
        }

        // 草稿完成才有匯出鈕，DraftCompletedAt 幾乎一定有值；沒有就退回最後更新時間。
        var stamp = (meeting.DraftCompletedAt ?? meeting.UpdatedAt).ToString("yyyyMMdd");

        return $"會議紀錄_{title}_{stamp}.pdf";
    }

    private static string FormatDate(DateTime? value)
        => value?.ToString("yyyy/MM/dd") ?? UnspecifiedText;

    private static string FormatDateTime(DateTime? value)
        => value?.ToString("yyyy/MM/dd HH:mm") ?? UnspecifiedText;

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
