using System.Net;
using System.Text;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 把 AI 問答對話組成可列印成 PDF 的 HTML 文件（整段，或單獨一則）。
///
/// 形狀比照 <see cref="MeetingDocumentExporter"/>：純函式、不碰任何 IO，
/// 實際跑瀏覽器的是 <see cref="HeadlessBrowserPdfRenderer"/>。
///
/// 回答本身是 Markdown，走 <see cref="MarkdownRenderer"/>——與畫面上看到的是同一份輸出。
/// </summary>
public static class AiChatDocumentExporter
{
    /// <summary>欄位無值時的顯示文字，與畫面上的表達一致。</summary>
    public const string UnspecifiedText = "未指定";

    private const string UnknownAskerName = "使用者";
    private const string AssistantName = "AI 助理";

    /// <summary>
    /// 對話版面。刻意不套用會議紀錄的 <c>.doc-title</c> 以外的版面樣式——
    /// 那份是給單一篇文件用的，對話是一則一則的。
    /// </summary>
    private const string ChatStyle = """
        .chat-message { margin: 0 0 14px; padding: 10px 12px; border: 1px solid #dde3ea; border-radius: 6px; }
        .chat-message-user { background: #f5f7fa; }
        .chat-speaker {
            display: flex;
            gap: 8px;
            margin-bottom: 4px;
            font-size: 10pt;
            font-weight: 600;
            color: #4a5a6b;
            /* 發話者不要落在頁尾、內容卻翻到下一頁。長答案本身仍允許跨頁，
               硬加 break-inside: avoid 只會讓整頁空一大片。 */
            break-after: avoid;
        }
        .chat-time { font-weight: 400; }
        .chat-content > :first-child { margin-top: 0; }
        .chat-content > :last-child { margin-bottom: 0; }
        """;

    /// <summary>整段對話。</summary>
    public static string BuildConversationHtml(
        string? targetName,
        IReadOnlyList<AiChatMessageItem> messages,
        DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var name = Fallback(targetName);
        var builder = StartDocument($"AI 問答：{name}");

        AppendHeader(
            builder,
            name,
            [("對象", name), ("訊息則數", messages.Count.ToString()), ("匯出時間", FormatDateTime(exportedAt))]);

        for (var index = 0; index < messages.Count; index++)
        {
            AppendMessage(builder, messages[index]);
        }

        return EndDocument(builder);
    }

    /// <summary>單獨一則訊息。<paramref name="ordinal"/> 是它在整段對話裡的順序（從 1 起算）。</summary>
    public static string BuildMessageHtml(
        string? targetName,
        AiChatMessageItem message,
        int ordinal,
        DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        var name = Fallback(targetName);
        var builder = StartDocument($"AI 問答：{name}（第 {ordinal} 則）");

        AppendHeader(
            builder,
            $"{name}（第 {ordinal} 則）",
            [("對象", name), ("發話者", ResolveSpeaker(message)), ("時間", FormatDateTime(message.CreatedAt))]);

        AppendMessage(builder, message);

        return EndDocument(builder);
    }

    /// <summary>整段對話的下載檔名：<c>AI問答_{對象}_{匯出日}.pdf</c>。</summary>
    public static string BuildConversationFileName(string? targetName, DateTime exportedAt)
        => $"AI問答_{SafeName(targetName)}_{exportedAt:yyyyMMdd}.pdf";

    /// <summary>單則訊息的下載檔名：<c>AI問答_{對象}_第{n}則_{匯出日}.pdf</c>。</summary>
    public static string BuildMessageFileName(string? targetName, int ordinal, DateTime exportedAt)
        => $"AI問答_{SafeName(targetName)}_第{ordinal}則_{exportedAt:yyyyMMdd}.pdf";

    private static StringBuilder StartDocument(string title)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-Hant\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(title)}</title>");
        builder.AppendLine($"<style>{ExportPrintStyles.Base}{Environment.NewLine}{ChatStyle}</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");

        return builder;
    }

    private static void AppendHeader(
        StringBuilder builder,
        string heading,
        IReadOnlyList<(string Label, string Value)> rows)
    {
        builder.AppendLine($"<h1 class=\"doc-title\">AI 問答：{Escape(heading)}</h1>");
        builder.AppendLine("<dl class=\"doc-meta\">");

        foreach (var (label, value) in rows)
        {
            builder.AppendLine(
                $"<div class=\"doc-meta-row\"><dt>{Escape(label)}</dt><dd>{Escape(value)}</dd></div>");
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("<hr class=\"doc-rule\">");

        // 內文包在 .doc-body 裡，Markdown 產出的標題、清單、表格才吃得到共用的列印樣式。
        builder.AppendLine("<main class=\"doc-body\">");
    }

    private static void AppendMessage(StringBuilder builder, AiChatMessageItem message)
    {
        var roleClass = message.IsUser ? "chat-message chat-message-user" : "chat-message";

        builder.AppendLine($"<section class=\"{roleClass}\">");
        builder.AppendLine(
            $"<div class=\"chat-speaker\">{Escape(ResolveSpeaker(message))}"
            + $"<span class=\"chat-time\">{Escape(FormatDateTime(message.CreatedAt))}</span></div>");
        builder.AppendLine("<div class=\"chat-content\">");
        builder.AppendLine(MarkdownRenderer.ToHtml(message.Content).TrimEnd());
        builder.AppendLine("</div>");
        builder.AppendLine("</section>");
    }

    private static string EndDocument(StringBuilder builder)
    {
        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static string ResolveSpeaker(AiChatMessageItem message)
        => message.IsUser
            ? Fallback(message.AskedBy, UnknownAskerName)
            : AssistantName;

    private static string SafeName(string? targetName)
    {
        var safe = ExportFileNameBuilder.SafeTitle(targetName);

        return string.IsNullOrWhiteSpace(safe) ? UnspecifiedText : safe;
    }

    /// <summary>
    /// 表頭與發話者名稱都是使用者輸入，必須逸出。
    /// 內文不經過這裡——它走 <see cref="MarkdownRenderer"/>，已由該處理器負責。
    /// </summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string FormatDateTime(DateTime value) => value.ToString("yyyy/MM/dd HH:mm");

    private static string Fallback(string? value, string fallback = UnspecifiedText)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
