using MeetingRecord.Business.Services.Export;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 會議紀錄匯出成 Markdown 的單元測試。
/// 這裡刻意只測純函式——不碰資料庫、不做 JS interop。
/// </summary>
public sealed class MeetingMarkdownExporterTests
{
    #region 文件結構

    [Fact]
    public void BuildDocument_ShouldContainHeaderFields()
    {
        var meeting = NewMeeting();

        var doc = MeetingMarkdownExporter.BuildDocument(meeting, "Q3 產品改版專案");

        Assert.Contains("# 會議紀錄：需求確認會議", doc);
        Assert.Contains("- 專案：Q3 產品改版專案", doc);
        Assert.Contains("- 會議：需求確認會議", doc);
        Assert.Contains("- 會議日期：2026/08/28", doc);
        Assert.Contains("- 使用提示詞：標準會議紀錄", doc);
        Assert.Contains("- 產生時間：2026/09/01 11:22", doc);
    }

    [Fact]
    public void BuildDocument_ShouldKeepDraftContentVerbatim()
    {
        // DraftContent 本身就是 Markdown，匯出時不做任何轉換。
        var draft = "## 會議摘要\n本次會議確認三件事。\n\n## 決議事項\n1. 第一項\n2. 第二項";
        var meeting = NewMeeting(draft: draft);

        var doc = MeetingMarkdownExporter.BuildDocument(meeting);

        Assert.Contains(draft, doc);
    }

    [Fact]
    public void BuildDocument_ShouldSeparateHeaderFromBody()
    {
        var meeting = NewMeeting();

        var doc = MeetingMarkdownExporter.BuildDocument(meeting);

        var separatorIndex = doc.IndexOf("---", StringComparison.Ordinal);
        Assert.True(separatorIndex > 0, "應有分隔線");
        Assert.True(doc.IndexOf("- 產生時間：", StringComparison.Ordinal) < separatorIndex, "表頭應在分隔線之前");
        Assert.True(doc.IndexOf("## 會議摘要", StringComparison.Ordinal) > separatorIndex, "內文應在分隔線之後");
    }

    [Fact]
    public void BuildDocument_ShouldFallBackToProjectTitleOnModel_WhenNotSupplied()
    {
        var meeting = NewMeeting();
        meeting.ProjectTitle = "客戶訪談專案";

        var doc = MeetingMarkdownExporter.BuildDocument(meeting);

        Assert.Contains("- 專案：客戶訪談專案", doc);
    }

    [Fact]
    public void BuildDocument_ShouldWriteUnspecified_WhenOptionalFieldsAreMissing()
    {
        var meeting = NewMeeting();
        meeting.MeetingDate = null;
        meeting.DraftPromptTemplateName = null;
        meeting.DraftCompletedAt = null;
        meeting.ProjectTitle = null;

        var doc = MeetingMarkdownExporter.BuildDocument(meeting);

        Assert.Contains("- 專案：未指定", doc);
        Assert.Contains("- 會議日期：未指定", doc);
        Assert.Contains("- 使用提示詞：未指定", doc);
        Assert.Contains("- 產生時間：未指定", doc);
    }

    [Fact]
    public void BuildDocument_ShouldThrow_WhenMeetingIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => MeetingMarkdownExporter.BuildDocument(null!));
    }

    #endregion

    #region 檔名

    [Fact]
    public void BuildFileName_ShouldComposeExpectedPattern()
    {
        var meeting = NewMeeting();

        Assert.Equal("會議紀錄_需求確認會議_20260901.md", MeetingMarkdownExporter.BuildFileName(meeting));
    }

    [Fact]
    public void BuildFileName_ShouldStripMediaExtension()
    {
        // 會議標題常常就是上傳的檔名，帶著 .m4a 當文件名很怪。
        var meeting = NewMeeting(title: "2026-08-27_客戶訪談錄音.m4a");

        Assert.Equal("會議紀錄_2026-08-27_客戶訪談錄音_20260901.md", MeetingMarkdownExporter.BuildFileName(meeting));
    }

    [Theory]
    [InlineData("專案/會議:紀錄", "會議紀錄_專案_會議_紀錄_20260901.md")]
    [InlineData("報告*草稿?", "會議紀錄_報告_草稿_20260901.md")]
    [InlineData("A<B>C|D", "會議紀錄_A_B_C_D_20260901.md")]
    public void BuildFileName_ShouldRemoveInvalidCharacters(string title, string expected)
    {
        var name = MeetingMarkdownExporter.BuildFileName(NewMeeting(title: title));

        Assert.Equal(expected, name);
        // 用 char 版的 IndexOf（序數比對）；字串版的 DoesNotContain 是文化相關的，
        // 控制字元會被視為可忽略而在任何字串中都「找得到」。
        Assert.All(Path.GetInvalidFileNameChars(), ch => Assert.True(name.IndexOf(ch) < 0, $"檔名不該含有 U+{(int)ch:X4}"));
    }

    [Fact]
    public void BuildFileName_ShouldCollapseWhitespaceIntoSingleUnderscore()
    {
        var name = MeetingMarkdownExporter.BuildFileName(NewMeeting(title: "第一季   檢討   會議"));

        Assert.Equal("會議紀錄_第一季_檢討_會議_20260901.md", name);
    }

    [Fact]
    public void BuildFileName_ShouldTruncateOverlyLongTitle()
    {
        var name = MeetingMarkdownExporter.BuildFileName(NewMeeting(title: new string('長', 200)));

        // 「會議紀錄_」+ 60 字 +「_20260901.md」
        Assert.Equal($"會議紀錄_{new string('長', MeetingMarkdownExporter.MaxTitleLengthInFileName)}_20260901.md", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void BuildFileName_ShouldFallBack_WhenTitleYieldsNothingUsable(string title)
    {
        var name = MeetingMarkdownExporter.BuildFileName(NewMeeting(title: title));

        Assert.Equal("會議紀錄_未指定_20260901.md", name);
    }

    [Fact]
    public void BuildFileName_ShouldUseUpdatedAt_WhenDraftCompletedAtIsMissing()
    {
        var meeting = NewMeeting();
        meeting.DraftCompletedAt = null;
        meeting.UpdatedAt = new DateTime(2026, 7, 15);

        Assert.Equal("會議紀錄_需求確認會議_20260715.md", MeetingMarkdownExporter.BuildFileName(meeting));
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
