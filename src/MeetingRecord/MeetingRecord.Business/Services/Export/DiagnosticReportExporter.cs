using System.Net;
using System.Text;
using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Business.Services.Export;

/// <summary>
/// 把「監控與診斷」的 AI 分析結果組成可列印成 PDF 的 HTML（0.4.121：AI 日誌分析）。
///
/// <para>形狀比照 <see cref="AiChatDocumentExporter"/>：純函式、不碰任何 IO，實際跑瀏覽器的是
/// <see cref="HeadlessBrowserPdfRenderer"/>。內文是 Markdown，走 <see cref="MarkdownRenderer"/>——與畫面上看到的同一份輸出。</para>
///
/// <para>附錄放「實際送出的原始資料」：AI 的結論要能回頭對照它看到了什麼。</para>
/// </summary>
public static class DiagnosticReportExporter
{
    /// <summary>每份報告都要有的免責說明。</summary>
    public const string Disclaimer = "本報告由 AI 依下列資料生成，內容僅供參考，請以原始資料為準。";

    private const string ReportStyle = """
        .doc-disclaimer { margin: 0 0 14px; color: #8a5a1f; font-size: 9.5pt; }
        .doc-appendix { break-before: page; }
        .doc-appendix h2 { font-size: 13pt; margin: 0 0 8px; }
        .doc-appendix pre {
            background: #f5f7fa;
            padding: 10px 12px;
            border-radius: 4px;
            white-space: pre-wrap;
            word-break: break-all;
            font-family: Consolas, "Courier New", monospace;
            font-size: 8.5pt;
            line-height: 1.5;
        }
        """;

    /// <summary>
    /// 組出報告 HTML。
    /// </summary>
    /// <param name="title">報告標題，例如「AI 日誌分析報告」。</param>
    /// <param name="meta">表頭的「欄位：值」列（查詢區間、分析筆數、模型……）。</param>
    /// <param name="markdown">AI 回覆的 Markdown。</param>
    /// <param name="appendixTitle">附錄標題；與 <paramref name="appendixText"/> 任一為空就不輸出附錄。</param>
    /// <param name="appendixText">附錄內容（原樣、逸出後放進 <c>&lt;pre&gt;</c>）。</param>
    public static string BuildHtml(
        string title,
        IReadOnlyList<(string Label, string Value)> meta,
        string? markdown,
        string? appendixTitle,
        string? appendixText,
        DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-Hant\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(title)}</title>");
        builder.AppendLine($"<style>{ExportPrintStyles.Base}{Environment.NewLine}{ReportStyle}</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");

        builder.AppendLine($"<h1 class=\"doc-title\">{Escape(title)}</h1>");
        builder.AppendLine("<dl class=\"doc-meta\">");
        foreach (var (label, value) in meta.Append(("匯出時間", exportedAt.ToString("yyyy/MM/dd HH:mm"))))
        {
            builder.AppendLine($"<div class=\"doc-meta-row\"><dt>{Escape(label)}</dt><dd>{Escape(value)}</dd></div>");
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("<hr class=\"doc-rule\">");
        builder.AppendLine($"<p class=\"doc-disclaimer\">{Escape(Disclaimer)}</p>");

        // 內文包在 .doc-body 裡，Markdown 產出的標題、清單才吃得到共用的列印樣式。
        builder.AppendLine("<main class=\"doc-body\">");
        builder.AppendLine(MarkdownRenderer.ToHtml(markdown).TrimEnd());
        builder.AppendLine("</main>");

        if (!string.IsNullOrWhiteSpace(appendixTitle) && !string.IsNullOrEmpty(appendixText))
        {
            builder.AppendLine("<section class=\"doc-appendix\">");
            builder.AppendLine($"<h2>{Escape(appendixTitle)}</h2>");
            builder.AppendLine($"<pre>{Escape(appendixText)}</pre>");
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    /// <summary>下載檔名：<c>{prefix}_{yyyyMMdd-HHmmss}.pdf</c>，例如 <c>AI日誌分析_20261014-153000.pdf</c>。</summary>
    public static string BuildFileName(string prefix, DateTime exportedAt)
    {
        var safe = ExportFileNameBuilder.SafeTitle(prefix);
        return $"{(string.IsNullOrWhiteSpace(safe) ? "AI分析" : safe)}_{exportedAt:yyyyMMdd-HHmmss}.pdf";
    }

    /// <summary>表頭與附錄是使用者輸入或日誌原文，一律逸出；內文由 MarkdownRenderer 負責。</summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
