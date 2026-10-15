using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Components.Views.AiManagement;
using MeetingRecord.Web.Export;

namespace MeetingRecord.Tests;

/// <summary>
/// Token 用量報表（1.0.31）的 PDF 來源 HTML，以及 Token 用量頁的 CSV 欄位。
/// </summary>
public sealed class TokenUsageReportExporterTests
{
    private static readonly DateTime ExportedAt = new(2026, 10, 15, 9, 30, 0);

    [Fact]
    public void BuildHtml_ShouldContainEverySection()
    {
        var html = TokenUsageReportExporter.BuildHtml(Request(Report(), [Detail(1)]));

        Assert.Contains("<h1 class=\"doc-title\">Token 用量報表</h1>", html);
        foreach (var section in new[] { "合計", "每日", "依使用者", "依功能", "依模型", "明細" })
        {
            Assert.Contains($"<h2>{section}</h2>", html);
        }

        Assert.Contains("2026/10/01～2026/10/15", html);
        Assert.Contains("1.0.31", html);
        Assert.Contains("2026/10/15 09:30", html);
        Assert.Contains("NT$ 12.00", html);
    }

    [Fact]
    public void BuildHtml_ShouldEscapeExternalStrings()
    {
        // 使用者名稱、模型、篩選值都是外部來源，不可原樣進 HTML。
        var report = Report() with
        {
            ByUser = [new AiUsageGroupRow("<script>alert(1)</script>", 1, 0, 1, 1, 0, 1m, "NT$ 1.00", 0)],
        };
        var request = Request(report, [Detail(1) with { Model = "<b>m</b>" }]) with
        {
            Filters = [("模型", "<img src=x onerror=alert(1)>")],
        };

        var html = TokenUsageReportExporter.BuildHtml(request);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>m</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildHtml_ShouldCapDetailRowsAndSaySo()
    {
        var details = Enumerable.Range(1, TokenUsageReportExporter.MaxDetailRows + 1).Select(Detail).ToList();

        // 呼叫端只多讀一筆，真正的總數來自合計。
        var html = TokenUsageReportExporter.BuildHtml(Request(Report() with { CallCount = 5000 }, details));

        Assert.Contains("完整資料請用 CSV 匯出", html);
        Assert.Contains("共 5,000 筆", html);
        Assert.Equal(TokenUsageReportExporter.MaxDetailRows, CountOccurrences(html, "王小明-明細"));
    }

    [Fact]
    public void BuildHtml_ShouldNotThrow_WhenThereIsNoData()
    {
        var empty = new AiUsageReport(0, 0, 0, 0, 0, 0m, "NT$ 0", 0, 0, [], false, [], [], [], [], [], []);

        var html = TokenUsageReportExporter.BuildHtml(Request(empty, []));

        Assert.Contains("沒有資料", html);
    }

    [Fact]
    public void BuildFileName_ShouldUseTheTimestamp()
        => Assert.Equal("Token用量報表_20261015-093000.pdf", TokenUsageReportExporter.BuildFileName(ExportedAt));

    [Fact]
    public void View_CsvColumns_ShouldKeepOrderAndCarryTheCallId()
    {
        var headers = TokenUsageView.CsvColumns.Select(x => x.Header).ToList();

        Assert.Equal("時間", headers[0]);
        Assert.Equal("呼叫識別碼", headers[^1]);
        Assert.Contains("估算金額", headers);
        Assert.Contains("匯率", headers);
        Assert.Contains("換算金額", headers);

        var text = CsvExport.ToCsvText(TokenUsageView.CsvColumns, [Detail(1)]);
        Assert.Contains("0.375", text);
        Assert.Contains("12.00", text);
    }

    private static TokenUsageReportRequest Request(AiUsageReport report, IReadOnlyList<AiUsageDetail> details)
        => new("1.0.31", "support", ExportedAt, "2026/10/01～2026/10/15", [("功能", "AI 問答")], report, details);

    private static AiUsageReport Report()
    {
        var group = new AiUsageGroupRow("王小明", 2, 0, 200, 100, 0, 12m, "NT$ 12.00", 0);
        return new AiUsageReport(
            2, 0, 200, 100, 0, 12m, "NT$ 12.00", 0, 0,
            [new AiUsageDailyRow(new DateOnly(2026, 10, 15), 2, 200, 100, 0, 12m, "NT$ 12.00", 0)],
            true,
            [group],
            [group with { Key = "AI 問答" }],
            [group with { Key = "gpt-4o-mini" }],
            [], [], []);
    }

    private static AiUsageDetail Detail(int id)
        => new(
            id, Guid.NewGuid(), ExportedAt.AddMinutes(-id), AiUsageFeature.AiChat, AiUsageOutcome.Succeeded,
            "AzureOpenAI", "gpt-4o-mini", 1, "王小明-明細", null, null, 100, 50, null, null, false,
            0.375m, "USD", 0.15m, 0.6m, null, 32m, "TWD", 12.00m, "NT$ 12.00", null);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
