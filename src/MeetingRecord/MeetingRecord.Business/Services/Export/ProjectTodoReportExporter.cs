using System.Globalization;
using System.Net;
using System.Text;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Business.Services.Export;

/// <summary>專案待辦總覽的輸入（1.0.54）。</summary>
/// <param name="Todos">該專案看得到的全部待辦（主待辦與子項目都有、已完成也在內），順序即輸出順序。</param>
public sealed record ProjectTodoReportRequest(
    string SystemVersion,
    string OperatorAccount,
    DateTime ExportedAt,
    ProjectAdapterModel Project,
    IReadOnlyList<TodoAdapterModel> Todos);

/// <summary>
/// 把單一專案的所有待辦組成可列印成 PDF 的 HTML，用來看進度（1.0.54）。
///
/// <para>形狀比照 <see cref="TokenUsageReportExporter"/>：純函式、不碰任何 IO，實際跑瀏覽器的是
/// <see cref="HeadlessBrowserPdfRenderer"/>。</para>
///
/// <para>⚠️ 所有字串一律 HTML 逸出：待辦標題、負責人、會議標題都是使用者輸入。</para>
/// </summary>
public static class ProjectTodoReportExporter
{
    private const string ReportStyle = """
        @page { size: A4 landscape; margin: 14mm 12mm; }
        h2 { font-size: 13pt; margin: 18px 0 6px; break-after: avoid; }
        table.report { width: 100%; border-collapse: collapse; font-size: 9pt; }
        table.report th, table.report td { border: 1px solid #c8d1da; padding: 3px 6px; vertical-align: top; }
        table.report th { background: #f0f3f6; text-align: left; }
        table.report td.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        table.report tr { break-inside: avoid; }
        table.report tr.done td { color: #8a97a5; }
        table.report tr.overdue td { color: #b42318; }
        td.sub { padding-left: 20px; }
        .bar-track { height: 10px; background: #e6ebf0; border-radius: 3px; }
        .bar { height: 10px; background: #4c9a6a; border-radius: 3px; }
        .muted { color: #6b7a89; font-size: 9pt; }
        """;

    public static string BuildHtml(ProjectTodoReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = request.Project;
        var todos = request.Todos;
        var title = $"{project.Title} 待辦總覽";
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
        (string Label, string Value)[] meta =
        [
            ("專案負責人", string.IsNullOrWhiteSpace(project.Owner) ? "未指定" : project.Owner),
            ("期程", $"{FormatDate(project.StartDate)}～{FormatDate(project.EndDate)}"),
            ("專案狀態", project.Status),
            ("系統版本", request.SystemVersion),
            ("匯出者", request.OperatorAccount),
            ("匯出時間", request.ExportedAt.ToString("yyyy/MM/dd HH:mm")),
        ];
        foreach (var (label, value) in meta)
        {
            builder.AppendLine($"<div class=\"doc-meta-row\"><dt>{Escape(label)}</dt><dd>{Escape(value)}</dd></div>");
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("<hr class=\"doc-rule\">");

        // ───────── 進度摘要（含子項目） ─────────
        var total = todos.Count;
        var completed = todos.Count(x => x.IsCompleted);
        var inProgress = todos.Count(x => !x.IsCompleted && x.Status == TodoAdapterModel.StatusOptions[1]);
        var pending = total - completed - inProgress;
        var overdue = todos.Count(x => x.IsOverdue);
        // 刻意捨去不四捨五入（同 TodoOwnerSummary.CompletionPercent）：199/200 不能顯示成 100%。
        var percent = total == 0 ? 0 : completed * 100 / total;

        builder.AppendLine("<h2>進度摘要</h2>");
        builder.AppendLine("<table class=\"report\"><thead><tr>");
        AppendHeaders(builder, "總數", "已完成", "進行中", "待辦", "逾期", "完成率", "");
        builder.AppendLine("</tr></thead><tbody><tr>");
        foreach (var count in new[] { total, completed, inProgress, pending, overdue })
        {
            AppendNumber(builder, count.ToString(CultureInfo.InvariantCulture));
        }

        AppendNumber(builder, $"{percent}%");
        builder.AppendLine($"<td style=\"width: 35%\"><div class=\"bar-track\"><div class=\"bar\" style=\"width: {percent}%\"></div></div></td>");
        builder.AppendLine("</tr></tbody></table>");
        builder.AppendLine($"<p class=\"muted\">完成率＝已完成待辦÷全部待辦（含子項目）。專案設定的完成度是 {project.CompletionPercentage}%，由人工填寫，與完成率分開計算。</p>");

        // ───────── 明細 ─────────
        builder.AppendLine("<h2>待辦明細</h2>");
        if (total == 0)
        {
            builder.AppendLine("<p class=\"muted\">這個專案目前沒有待辦。</p>");
        }
        else
        {
            builder.AppendLine("<table class=\"report\"><thead><tr>");
            AppendHeaders(builder, "#", "標題", "負責人", "優先", "狀態", "期限", "逾期", "來源會議");
            builder.AppendLine("</tr></thead><tbody>");

            var number = 0;
            foreach (var (todo, isSub) in OrderParentsThenChildren(todos))
            {
                number++;
                var rowClass = todo.IsCompleted ? " class=\"done\"" : todo.IsOverdue ? " class=\"overdue\"" : string.Empty;
                var titleText = todo.Title;
                if (!isSub)
                {
                    var children = todos.Where(x => x.ParentTodoId == todo.Id).ToList();
                    if (children.Count > 0)
                    {
                        titleText += $"（子項 {children.Count(x => x.IsCompleted)}/{children.Count}）";
                    }
                }

                builder.AppendLine($"<tr{rowClass}>");
                AppendNumber(builder, number.ToString(CultureInfo.InvariantCulture));
                builder.Append(isSub ? $"<td class=\"sub\">└ {Escape(titleText)}</td>" : $"<td>{Escape(titleText)}</td>");
                AppendCell(builder, string.IsNullOrWhiteSpace(todo.Owner) ? "未指定" : todo.Owner);
                AppendCell(builder, todo.Priority);
                AppendCell(builder, todo.Status);
                AppendCell(builder, todo.DueDateOnlyText);
                AppendNumber(builder, todo.IsOverdue ? $"{todo.OverdueDays} 天" : string.Empty);
                AppendCell(builder, todo.MeetingTitle ?? "—");
                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody></table>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    /// <summary>下載檔名：<c>{專案名稱}_待辦總覽_yyyyMMdd.pdf</c>。</summary>
    public static string BuildFileName(ProjectAdapterModel project, DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(project);

        var title = ExportFileNameBuilder.SafeTitle(project.Title);
        return $"{(title.Length == 0 ? "專案" : title)}_待辦總覽_{exportedAt:yyyyMMdd}.pdf";
    }

    /// <summary>
    /// 主待辦依輸入順序，子項目緊接在它的主待辦後面。主待辦不在清單裡的子項目當成主層列出，不讓它消失。
    /// </summary>
    private static IEnumerable<(TodoAdapterModel Todo, bool IsSub)> OrderParentsThenChildren(IReadOnlyList<TodoAdapterModel> todos)
    {
        var ids = todos.Select(x => x.Id).ToHashSet();
        foreach (var todo in todos.Where(x => x.ParentTodoId is not { } parentId || !ids.Contains(parentId)))
        {
            yield return (todo, false);
            foreach (var child in todos.Where(x => x.ParentTodoId == todo.Id))
            {
                yield return (child, true);
            }
        }
    }

    private static string FormatDate(DateTime? value) => value?.ToString("yyyy/MM/dd") ?? "未指定";

    private static void AppendHeaders(StringBuilder builder, params string[] headers)
    {
        foreach (var header in headers)
        {
            builder.Append($"<th>{Escape(header)}</th>");
        }

        builder.AppendLine();
    }

    private static void AppendCell(StringBuilder builder, string value) => builder.Append($"<td>{Escape(value)}</td>");

    private static void AppendNumber(StringBuilder builder, string value) => builder.Append($"<td class=\"num\">{Escape(value)}</td>");

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
