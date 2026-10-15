using System.Globalization;
using System.Net;
using System.Text;
using MeetingRecord.Business.Services.AiUsage;

namespace MeetingRecord.Business.Services.Export;

/// <summary>Token 用量報表的輸入（1.0.31）。</summary>
/// <param name="RangeText">查詢區間的文字，例如「2026/10/01～2026/10/15」或「不限」。</param>
/// <param name="Filters">其他篩選條件（功能、使用者、模型、結果）的「欄位：值」。</param>
/// <param name="Details">明細列，新到舊；超過 <see cref="TokenUsageReportExporter.MaxDetailRows"/> 筆只輸出前面那些。</param>
public sealed record TokenUsageReportRequest(
    string SystemVersion,
    string OperatorAccount,
    DateTime ExportedAt,
    string RangeText,
    IReadOnlyList<(string Label, string Value)> Filters,
    AiUsageReport Report,
    IReadOnlyList<AiUsageDetail> Details);

/// <summary>
/// 把 Token 用量的篩選結果組成可列印成 PDF 的 HTML（1.0.31）。
///
/// <para>形狀比照 <see cref="DiagnosticReportExporter"/>：純函式、不碰任何 IO，實際跑瀏覽器的是
/// <see cref="HeadlessBrowserPdfRenderer"/>。Starter 用 MigraDoc 自己排版；本專案沿用既有的「HTML → 無頭瀏覽器」管線，
/// 中文字型交給瀏覽器，不必嵌字型檔。</para>
///
/// <para>⚠️ 所有字串一律 HTML 逸出：使用者名稱、模型名稱、錯誤訊息都是外部來源。</para>
/// </summary>
public static class TokenUsageReportExporter
{
    /// <summary>報表明細最多幾筆；完整資料請用 CSV。</summary>
    public const int MaxDetailRows = 200;

    private const string ReportStyle = """
        @page { size: A4 landscape; margin: 14mm 12mm; }
        h2 { font-size: 13pt; margin: 18px 0 6px; break-after: avoid; }
        table.report { width: 100%; border-collapse: collapse; font-size: 9pt; }
        table.report th, table.report td { border: 1px solid #c8d1da; padding: 3px 6px; }
        table.report th { background: #f0f3f6; text-align: left; }
        table.report td.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        table.report tr { break-inside: avoid; }
        .bar { height: 8px; background: #8aa4bf; border-radius: 2px; }
        .note { margin: 4px 0; color: #8a5a1f; font-size: 9pt; }
        .muted { color: #6b7a89; font-size: 9pt; }
        """;

    public static string BuildHtml(TokenUsageReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var report = request.Report;
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-Hant\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<title>Token 用量報表</title>");
        builder.AppendLine($"<style>{ExportPrintStyles.Base}{Environment.NewLine}{ReportStyle}</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");

        builder.AppendLine("<h1 class=\"doc-title\">Token 用量報表</h1>");
        builder.AppendLine("<dl class=\"doc-meta\">");
        IEnumerable<(string Label, string Value)> meta =
        [
            ("系統版本", request.SystemVersion),
            ("匯出者", request.OperatorAccount),
            ("查詢區間", request.RangeText),
            .. request.Filters,
            ("匯出時間", request.ExportedAt.ToString("yyyy/MM/dd HH:mm")),
        ];
        foreach (var (label, value) in meta)
        {
            builder.AppendLine($"<div class=\"doc-meta-row\"><dt>{Escape(label)}</dt><dd>{Escape(value)}</dd></div>");
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("<hr class=\"doc-rule\">");

        // ───────── 合計 ─────────
        builder.AppendLine("<h2>合計</h2>");
        builder.AppendLine("<table class=\"report\"><thead><tr>");
        AppendHeaders(builder, "呼叫次數", "失敗或取消", "輸入 token", "輸出 token", "轉錄時長", "估算金額");
        builder.AppendLine("</tr></thead><tbody><tr>");
        AppendNumber(builder, report.CallCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendNumber(builder, report.FailedCallCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendNumber(builder, report.InputTokens.ToString("N0", CultureInfo.InvariantCulture));
        AppendNumber(builder, report.OutputTokens.ToString("N0", CultureInfo.InvariantCulture));
        AppendNumber(builder, AiUsageMetrics.FormatAudio(report.AudioSeconds));
        AppendNumber(builder, report.CostText);
        builder.AppendLine("</tr></tbody></table>");

        if (report.UnpricedCallCount > 0)
        {
            builder.AppendLine($"<p class=\"note\">有 {report.UnpricedCallCount:N0} 次呼叫的模型未設定單價，金額未納入統計。</p>");
        }

        if (report.NoRateCallCount > 0)
        {
            builder.AppendLine($"<p class=\"note\">有 {report.NoRateCallCount:N0} 次呼叫沒有當時的匯率，金額未納入換算後統計。</p>");
        }

        builder.AppendLine("<p class=\"muted\">金額為估算：依呼叫當下的單價與匯率換算；失敗與取消的呼叫沒有用量資料，實際帳單可能略高。</p>");

        // ───────── 每日 ─────────
        builder.AppendLine("<h2>每日</h2>");
        if (report.Daily.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">沒有資料。</p>");
        }
        else
        {
            var maxCost = report.Daily.Max(x => x.Cost);
            builder.AppendLine("<table class=\"report\"><thead><tr>");
            AppendHeaders(builder, "日期", "金額", "", "次數", "輸入／輸出 token", "轉錄時長");
            builder.AppendLine("</tr></thead><tbody>");
            foreach (var day in report.Daily)
            {
                var width = maxCost > 0 ? (int)Math.Round(day.Cost / maxCost * 100m) : 0;
                builder.AppendLine("<tr>");
                AppendCell(builder, day.Date.ToString("yyyy/MM/dd (ddd)", CultureInfo.GetCultureInfo("zh-TW")));
                AppendNumber(builder, day.CostText);
                builder.AppendLine($"<td style=\"width: 30%\"><div class=\"bar\" style=\"width: {width}%\"></div></td>");
                AppendNumber(builder, day.CallCount.ToString("N0", CultureInfo.InvariantCulture));
                AppendNumber(builder, $"{AiUsageMetrics.FormatTokens(day.InputTokens)} / {AiUsageMetrics.FormatTokens(day.OutputTokens)}");
                AppendNumber(builder, AiUsageMetrics.FormatAudio(day.AudioSeconds));
                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody></table>");
            if (!report.IsDailyFilled)
            {
                builder.AppendLine($"<p class=\"muted\">區間沒有起訖或超過 {AiUsageAnalysisService.MaxFilledDays} 天，只列出有資料的日子。</p>");
            }
        }

        // ───────── 分組 ─────────
        AppendGroup(builder, "依使用者", "使用者", report.ByUser);
        AppendGroup(builder, "依功能", "功能", report.ByFeature);
        AppendGroup(builder, "依模型", "模型", report.ByModel);

        // ───────── 明細 ─────────
        builder.AppendLine("<h2>明細</h2>");
        if (request.Details.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">沒有資料。</p>");
        }
        else
        {
            builder.AppendLine("<table class=\"report\"><thead><tr>");
            AppendHeaders(builder, "時間", "功能", "結果", "使用者", "對象", "模型", "輸入／輸出 token", "轉錄時長", "估算金額");
            builder.AppendLine("</tr></thead><tbody>");
            foreach (var item in request.Details.Take(MaxDetailRows))
            {
                builder.AppendLine("<tr>");
                AppendCell(builder, item.OccurredAt.ToString("yyyy/MM/dd HH:mm:ss"));
                AppendCell(builder, item.FeatureText);
                AppendCell(builder, item.OutcomeText);
                AppendCell(builder, item.UserName ?? "（系統）");
                AppendCell(builder, item.TargetText);
                AppendCell(builder, item.Model);
                AppendNumber(builder, item.InputTokens is { } input && item.OutputTokens is { } output
                    ? $"{input:N0} / {output:N0}"
                    : "—");
                AppendNumber(builder, item.AudioSeconds is { } seconds
                    ? AiUsageMetrics.FormatAudio(seconds) + (item.IsAudioDurationEstimated ? "（推估）" : string.Empty)
                    : "—");
                AppendNumber(builder, item.CostText);
                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody></table>");
            if (request.Details.Count > MaxDetailRows)
            {
                // 總筆數取合計：呼叫端只多讀一筆用來判斷是否超過上限，Details.Count 不是真正的總數。
                builder.AppendLine($"<p class=\"note\">明細只列出最新 {MaxDetailRows:N0} 筆（共 {Math.Max(report.CallCount, request.Details.Count):N0} 筆），完整資料請用 CSV 匯出。</p>");
            }
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    /// <summary>下載檔名：<c>Token用量報表_yyyyMMdd-HHmmss.pdf</c>。</summary>
    public static string BuildFileName(DateTime exportedAt) => $"Token用量報表_{exportedAt:yyyyMMdd-HHmmss}.pdf";

    private static void AppendGroup(StringBuilder builder, string title, string keyTitle, IReadOnlyList<AiUsageGroupRow> rows)
    {
        builder.AppendLine($"<h2>{Escape(title)}</h2>");
        if (rows.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">沒有資料。</p>");
            return;
        }

        builder.AppendLine("<table class=\"report\"><thead><tr>");
        AppendHeaders(builder, keyTitle, "次數", "失敗", "輸入 token", "輸出 token", "轉錄時長", "估算金額", "未計入");
        builder.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            builder.AppendLine("<tr>");
            AppendCell(builder, row.Key);
            AppendNumber(builder, row.CallCount.ToString("N0", CultureInfo.InvariantCulture));
            AppendNumber(builder, row.FailedCount.ToString("N0", CultureInfo.InvariantCulture));
            AppendNumber(builder, row.InputTokens.ToString("N0", CultureInfo.InvariantCulture));
            AppendNumber(builder, row.OutputTokens.ToString("N0", CultureInfo.InvariantCulture));
            AppendNumber(builder, AiUsageMetrics.FormatAudio(row.AudioSeconds));
            AppendNumber(builder, row.CostText);
            AppendNumber(builder, row.NotCountedCount.ToString("N0", CultureInfo.InvariantCulture));
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</tbody></table>");
    }

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
