using MeetingRecord.Business.Services.Export;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 會議紀錄匯出的單元測試（HTML 產生與檔名）。
/// 這裡刻意只測純函式——不碰資料庫、不做 JS interop。
/// </summary>
public sealed class MeetingDocumentExporterTests
{
    #region HTML 文件結構

    [Fact]
    public void BuildHtml_ShouldContainHeaderFields()
    {
        var meeting = NewMeeting();

        var doc = MeetingDocumentExporter.BuildHtml(meeting, "Q3 產品改版專案");

        Assert.Contains("會議紀錄：需求確認會議", doc);
        Assert.Contains("<dt>專案</dt><dd>Q3 產品改版專案</dd>", doc);
        Assert.Contains("<dt>會議</dt><dd>需求確認會議</dd>", doc);
        Assert.Contains("<dt>會議日期</dt><dd>2026/08/28</dd>", doc);
        Assert.Contains("<dt>使用提示詞</dt><dd>標準會議紀錄</dd>", doc);
        Assert.Contains("<dt>產生時間</dt><dd>2026/09/01 11:22</dd>", doc);
    }

    [Fact]
    public void BuildHtml_ShouldBeACompleteHtmlDocument()
    {
        // 要交給無頭瀏覽器以 file:// 載入，必須是完整文件而非片段。
        var doc = MeetingDocumentExporter.BuildHtml(NewMeeting());

        Assert.StartsWith("<!DOCTYPE html>", doc);
        Assert.Contains("<meta charset=\"utf-8\">", doc);
        Assert.Contains("@page", doc);
        Assert.EndsWith("</html>", doc.TrimEnd());
    }

    [Fact]
    public void BuildHtml_ShouldRenderMarkdownHeadingsAndLists()
    {
        var draft = "## 會議摘要\n本次會議確認三件事。\n\n## 決議事項\n1. 第一項\n2. 第二項";

        var doc = MeetingDocumentExporter.BuildHtml(NewMeeting(draft: draft));

        // advanced extensions 會加上 AutoIdentifiers（<h2 id="…">），所以只斷言標籤與內容。
        Assert.Contains("會議摘要</h2>", doc);
        Assert.Contains("<h2", doc);
        Assert.Contains("<ol>", doc);
        Assert.Contains("<li>第一項</li>", doc);
        // 轉成 HTML 之後就不該再看到原始的 Markdown 記號。
        Assert.DoesNotContain("## 決議事項", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_ShouldRenderMarkdownTables()
    {
        // 提示詞範本由使用者自行撰寫，LLM 很常用表格列決議／負責人／期限。
        // 少了 Markdig 的 advanced extensions，這裡會原封不動印出 |---|---|。
        var draft = "| 決議 | 負責人 |\n| --- | --- |\n| 導入新流程 | 王小明 |";

        var doc = MeetingDocumentExporter.BuildHtml(NewMeeting(draft: draft));

        Assert.Contains("<table", doc);
        Assert.Contains("<th>決議</th>", doc);
        Assert.Contains("<td>王小明</td>", doc);
        Assert.DoesNotContain("| --- |", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtml_ShouldEscapeHeaderFields()
    {
        // 會議標題是使用者輸入（常常就是上傳的檔名），不逸出會壞掉版面。
        var meeting = NewMeeting(title: "<script>alert(1)</script>");

        var doc = MeetingDocumentExporter.BuildHtml(meeting);

        Assert.DoesNotContain("<script>", doc, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", doc);
    }

    [Fact]
    public void BuildHtml_ShouldFallBackToProjectTitleOnModel_WhenNotSupplied()
    {
        var meeting = NewMeeting();
        meeting.ProjectTitle = "客戶訪談專案";

        var doc = MeetingDocumentExporter.BuildHtml(meeting);

        Assert.Contains("<dt>專案</dt><dd>客戶訪談專案</dd>", doc);
    }

    [Fact]
    public void BuildHtml_ShouldWriteUnspecified_WhenOptionalFieldsAreMissing()
    {
        var meeting = NewMeeting();
        meeting.MeetingDate = null;
        meeting.DraftPromptTemplateName = null;
        meeting.DraftCompletedAt = null;
        meeting.ProjectTitle = null;

        var doc = MeetingDocumentExporter.BuildHtml(meeting);

        Assert.Contains("<dt>專案</dt><dd>未指定</dd>", doc);
        Assert.Contains("<dt>會議日期</dt><dd>未指定</dd>", doc);
        Assert.Contains("<dt>使用提示詞</dt><dd>未指定</dd>", doc);
        Assert.Contains("<dt>產生時間</dt><dd>未指定</dd>", doc);
    }

    [Fact]
    public void BuildHtml_ShouldThrow_WhenMeetingIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => MeetingDocumentExporter.BuildHtml(null!));
    }

    #endregion

    #region 檔名

    [Fact]
    public void BuildFileName_ShouldComposeExpectedPattern()
    {
        var meeting = NewMeeting();

        Assert.Equal("會議紀錄_需求確認會議_20260901.pdf", MeetingDocumentExporter.BuildFileName(meeting));
    }

    [Fact]
    public void BuildFileName_ShouldStripMediaExtension()
    {
        // 會議標題常常就是上傳的檔名，帶著 .m4a 當文件名很怪。
        var meeting = NewMeeting(title: "2026-08-27_客戶訪談錄音.m4a");

        Assert.Equal("會議紀錄_2026-08-27_客戶訪談錄音_20260901.pdf", MeetingDocumentExporter.BuildFileName(meeting));
    }

    [Theory]
    [InlineData("專案/會議:紀錄", "會議紀錄_專案_會議_紀錄_20260901.pdf")]
    [InlineData("報告*草稿?", "會議紀錄_報告_草稿_20260901.pdf")]
    [InlineData("A<B>C|D", "會議紀錄_A_B_C_D_20260901.pdf")]
    public void BuildFileName_ShouldRemoveInvalidCharacters(string title, string expected)
    {
        var name = MeetingDocumentExporter.BuildFileName(NewMeeting(title: title));

        Assert.Equal(expected, name);
        // 用 char 版的 IndexOf（序數比對）；字串版的 DoesNotContain 是文化相關的，
        // 控制字元會被視為可忽略而在任何字串中都「找得到」。
        Assert.All(Path.GetInvalidFileNameChars(), ch => Assert.True(name.IndexOf(ch) < 0, $"檔名不該含有 U+{(int)ch:X4}"));
    }

    [Fact]
    public void BuildFileName_ShouldCollapseWhitespaceIntoSingleUnderscore()
    {
        var name = MeetingDocumentExporter.BuildFileName(NewMeeting(title: "第一季   檢討   會議"));

        Assert.Equal("會議紀錄_第一季_檢討_會議_20260901.pdf", name);
    }

    [Fact]
    public void BuildFileName_ShouldTruncateOverlyLongTitle()
    {
        var name = MeetingDocumentExporter.BuildFileName(NewMeeting(title: new string('長', 200)));

        // 「會議紀錄_」+ 60 字 +「_20260901.md」
        Assert.Equal($"會議紀錄_{new string('長', MeetingDocumentExporter.MaxTitleLengthInFileName)}_20260901.pdf", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void BuildFileName_ShouldFallBack_WhenTitleYieldsNothingUsable(string title)
    {
        var name = MeetingDocumentExporter.BuildFileName(NewMeeting(title: title));

        Assert.Equal("會議紀錄_未指定_20260901.pdf", name);
    }

    [Fact]
    public void BuildFileName_ShouldUseUpdatedAt_WhenDraftCompletedAtIsMissing()
    {
        var meeting = NewMeeting();
        meeting.DraftCompletedAt = null;
        meeting.UpdatedAt = new DateTime(2026, 7, 15);

        Assert.Equal("會議紀錄_需求確認會議_20260715.pdf", MeetingDocumentExporter.BuildFileName(meeting));
    }

    #endregion

    #region 測試輔助

    private static MeetingAdapterModel NewMeeting(
        string title = "需求確認會議",
        string draft = "## 會議摘要\n內容")
        => new()
        {
            Id = 1,
            Title = title,
            MeetingDate = new DateTime(2026, 8, 28),
            DraftContent = draft,
            DraftStatus = DraftStatus.Completed,
            DraftPromptTemplateName = "標準會議紀錄",
            DraftCompletedAt = new DateTime(2026, 9, 1, 11, 22, 0),
            UpdatedAt = new DateTime(2026, 9, 1, 11, 22, 0),
        };

    #endregion
}
