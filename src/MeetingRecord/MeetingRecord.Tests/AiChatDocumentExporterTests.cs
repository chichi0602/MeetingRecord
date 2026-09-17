using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Export;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 問答匯出 PDF 的單元測試（HTML 產生與檔名）。
/// 與 <see cref="MeetingDocumentExporterTests"/> 同一個慣例：只測純函式，不跑瀏覽器。
/// </summary>
public sealed class AiChatDocumentExporterTests
{
    private static readonly DateTime ExportedAt = new(2026, 9, 15, 14, 30, 0);

    private static AiChatMessageItem User(string content, string? askedBy = "王小明")
        => new(AiChatService.UserRole, content, askedBy, new DateTime(2026, 9, 15, 10, 0, 0));

    private static AiChatMessageItem Assistant(string content)
        => new(AiChatService.AssistantRole, content, null, new DateTime(2026, 9, 15, 10, 1, 0));

    #region 整段對話

    [Fact]
    public void BuildConversationHtml_ShouldBeACompleteHtmlDocument()
    {
        // 要交給無頭瀏覽器以 file:// 載入，必須是完整文件而非片段。
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "Q3 產品改版專案", [User("問題"), Assistant("回答")], ExportedAt);

        Assert.StartsWith("<!DOCTYPE html>", doc);
        Assert.Contains("<meta charset=\"utf-8\">", doc);
        Assert.Contains("@page", doc);
        Assert.EndsWith("</html>", doc.TrimEnd());
    }

    [Fact]
    public void BuildConversationHtml_ShouldContainEveryMessageInOrder()
    {
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "Q3 產品改版專案",
            [User("第一個問題"), Assistant("第一個回答"), User("第二個問題"), Assistant("第二個回答")],
            ExportedAt);

        Assert.Contains("第一個問題", doc);
        Assert.Contains("第二個回答", doc);
        Assert.True(doc.IndexOf("第一個問題", StringComparison.Ordinal)
            < doc.IndexOf("第二個問題", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildConversationHtml_ShouldLabelSpeakers()
    {
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "專案", [User("問題"), Assistant("回答")], ExportedAt);

        Assert.Contains("王小明", doc);
        Assert.Contains("AI 助理", doc);
    }

    [Fact]
    public void BuildConversationHtml_ShouldFallBackWhenAskerIsUnknown()
    {
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "專案", [User("問題", askedBy: null)], ExportedAt);

        Assert.Contains("使用者", doc);
    }

    [Fact]
    public void BuildConversationHtml_ShouldEscapeSpeakerName()
    {
        // 發話者名稱是使用者輸入，不逸出就會變成注入點。
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "專案", [User("問題", askedBy: "王<小明>")], ExportedAt);

        Assert.Contains("王&lt;小明&gt;", doc);
        Assert.DoesNotContain("<小明>", doc);
    }

    [Fact]
    public void BuildConversationHtml_ShouldRenderMarkdownTables()
    {
        var markdown = """
            | 決議 | 負責人 |
            | --- | --- |
            | 改版 | 王小明 |
            """;

        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "專案", [Assistant(markdown)], ExportedAt);

        Assert.Contains("<table", doc);
        Assert.Contains("<td>改版</td>", doc);
    }

    [Fact]
    public void BuildConversationHtml_ShouldBlockDangerousLinks()
    {
        // PDF 這條路不比畫面安全：無頭瀏覽器讀的是 file://，script 會真的執行。
        // 這筆是「畫面與 PDF 共用同一條 Markdown 管線」的迴歸守衛。
        var doc = AiChatDocumentExporter.BuildConversationHtml(
            "專案", [Assistant("[點我](javascript:alert(1))")], ExportedAt);

        Assert.DoesNotContain("javascript:", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConversationHtml_EmptyConversation_ShouldStillProduceDocument()
    {
        var doc = AiChatDocumentExporter.BuildConversationHtml("專案", [], ExportedAt);

        Assert.StartsWith("<!DOCTYPE html>", doc);
        Assert.Contains("<dt>訊息則數</dt><dd>0</dd>", doc);
    }

    #endregion

    #region 單則訊息

    [Fact]
    public void BuildMessageHtml_ShouldContainOnlyThatMessage()
    {
        var doc = AiChatDocumentExporter.BuildMessageHtml(
            "專案", Assistant("只有這一則"), ordinal: 4, ExportedAt);

        Assert.Contains("只有這一則", doc);
        Assert.Contains("第 4 則", doc);
    }

    [Fact]
    public void BuildMessageHtml_ShouldShowSpeakerInHeader()
    {
        var doc = AiChatDocumentExporter.BuildMessageHtml(
            "專案", User("問題"), ordinal: 1, ExportedAt);

        Assert.Contains("<dt>發話者</dt><dd>王小明</dd>", doc);
    }

    #endregion

    #region 檔名

    [Fact]
    public void BuildConversationFileName_ShouldUseTargetNameAndDate()
    {
        var name = AiChatDocumentExporter.BuildConversationFileName("Q3 產品改版專案", ExportedAt);

        Assert.Equal("AI問答_Q3_產品改版專案_20260915.pdf", name);
    }

    [Fact]
    public void BuildMessageFileName_ShouldIncludeOrdinal()
    {
        var name = AiChatDocumentExporter.BuildMessageFileName("專案", ordinal: 7, ExportedAt);

        Assert.Equal("AI問答_專案_第7則_20260915.pdf", name);
    }

    [Fact]
    public void BuildConversationFileName_ShouldStripInvalidCharacters()
    {
        var name = AiChatDocumentExporter.BuildConversationFileName("a/b:c*d?e", ExportedAt);

        Assert.Equal("AI問答_a_b_c_d_e_20260915.pdf", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConversationFileName_ShouldFallBackWhenNameIsEmpty(string? targetName)
    {
        var name = AiChatDocumentExporter.BuildConversationFileName(targetName, ExportedAt);

        Assert.Equal("AI問答_未指定_20260915.pdf", name);
    }

    [Fact]
    public void BuildConversationFileName_ShouldTruncateLongNames()
    {
        var name = AiChatDocumentExporter.BuildConversationFileName(new string('專', 120), ExportedAt);

        Assert.Equal($"AI問答_{new string('專', 60)}_20260915.pdf", name);
    }

    [Fact]
    public void BuildConversationFileName_ShouldIncludeConversationTitle()
    {
        // ⚠️ 0.4.79 起一個對象底下有多段對話。少了標題，同一天匯出兩段會撞成同名檔案。
        var first = AiChatDocumentExporter.BuildConversationFileName("Q3 專案", ExportedAt, "合約條款討論");
        var second = AiChatDocumentExporter.BuildConversationFileName("Q3 專案", ExportedAt, "驗收範圍");

        Assert.Equal("AI問答_Q3_專案_合約條款討論_20260915.pdf", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildMessageFileName_ShouldIncludeConversationTitle()
    {
        var name = AiChatDocumentExporter.BuildMessageFileName("專案", ordinal: 7, ExportedAt, "驗收範圍");

        Assert.Equal("AI問答_專案_驗收範圍_第7則_20260915.pdf", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConversationFileName_ShouldOmitEmptyConversationTitle(string? title)
    {
        // 沒有標題就整段省略，不要留下一個突兀的空底線。
        var name = AiChatDocumentExporter.BuildConversationFileName("專案", ExportedAt, title);

        Assert.Equal("AI問答_專案_20260915.pdf", name);
    }

    #endregion
}
