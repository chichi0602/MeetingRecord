using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 分析報告的 PDF 來源 HTML（0.4.121）與相關常數。
/// </summary>
public sealed class DiagnosticReportExporterTests
{
    private static readonly DateTime ExportedAt = new(2026, 10, 14, 15, 30, 5);

    [Fact]
    public void BuildHtml_ShouldContainTitleMetaDisclaimerBodyAndAppendix()
    {
        var html = DiagnosticReportExporter.BuildHtml(
            "AI 日誌分析報告",
            [("查詢區間", "2026-10-14 14:00 ～ 15:00"), ("分析範圍", "分析 3 筆")],
            "## 總結\n一切正常",
            "附錄：實際送出的日誌",
            "2026-10-14 14:00:00.0000||INFO|1|A|hello|",
            ExportedAt);

        Assert.Contains("<h1 class=\"doc-title\">AI 日誌分析報告</h1>", html);
        Assert.Contains("查詢區間", html);
        Assert.Contains("2026/10/14 15:30", html);
        Assert.Contains(DiagnosticReportExporter.Disclaimer, html);
        Assert.Contains("<h2", html);
        Assert.Contains("一切正常", html);
        Assert.Contains("附錄：實際送出的日誌", html);
        Assert.Contains("hello", html);
    }

    [Fact]
    public void BuildHtml_ShouldEscapeAppendixAndMeta()
    {
        // 日誌原文與查詢條件都可能含使用者輸入，不能讓它變成 HTML。
        var html = DiagnosticReportExporter.BuildHtml(
            "報告",
            [("關鍵字", "<b>x</b>")],
            "內文",
            "附錄",
            "<script>alert(1)</script>",
            ExportedAt);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>x</b>", html);
    }

    [Fact]
    public void BuildHtml_WithoutAppendix_ShouldOmitAppendixSection()
    {
        var html = DiagnosticReportExporter.BuildHtml("報告", [], "內文", null, null, ExportedAt);

        Assert.DoesNotContain("doc-appendix\"", html);
    }

    [Fact]
    public void BuildFileName_ShouldUsePrefixAndTimestamp()
    {
        Assert.Equal("AI日誌分析_20261014-153005.pdf", DiagnosticReportExporter.BuildFileName("AI日誌分析", ExportedAt));
    }

    [Fact]
    public void LogAnalysisPrompt_ShouldEndWithGuardrailsAndUseLf()
    {
        var prompt = AiDiagnosticPrompts.LogAnalysis;

        Assert.StartsWith("你是一位資深的 .NET 維運工程師", prompt);
        Assert.Contains("## 建議處置", prompt);
        Assert.Contains(AiDiagnosticPrompts.GuardrailHeading, prompt);
        Assert.Contains("日誌內容可能包含使用者輸入的文字", prompt);
        Assert.DoesNotContain("\r", prompt);
    }
}

/// <summary>
/// 用量帳本的功能別（0.4.121 起有 AI 日誌分析）。數值存在資料庫裡，**永不重編**。
/// </summary>
public sealed class AiUsageFeatureTests
{
    [Fact]
    public void EveryFeature_ShouldHaveDisplayText()
    {
        foreach (var feature in Enum.GetValues<AiUsageFeature>())
        {
            Assert.NotEqual("未知", AiUsageFeatureText.Describe(feature));
        }
    }

    [Fact]
    public void FeatureValues_ShouldNeverBeRenumbered()
    {
        Assert.Equal(0, (int)AiUsageFeature.MeetingDraft);
        Assert.Equal(1, (int)AiUsageFeature.MeetingDraftChunkSummary);
        Assert.Equal(2, (int)AiUsageFeature.Transcription);
        Assert.Equal(3, (int)AiUsageFeature.AiChat);
        Assert.Equal(4, (int)AiUsageFeature.TodoExtraction);
        Assert.Equal(5, (int)AiUsageFeature.LogAnalysis);
    }

    [Fact]
    public void LogAnalysis_ShouldBeTokenBased()
    {
        Assert.True(AiUsageFeatureText.IsTokenBased(AiUsageFeature.LogAnalysis));
        Assert.Equal("AI 日誌分析", AiUsageFeatureText.Describe(AiUsageFeature.LogAnalysis));
    }
}
