using MeetingRecord.Business.Services.AiChat;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 問答脈絡組裝的單元測試。重點在「超出預算時怎麼取捨」與
/// 「被截斷／被跳過的來源有沒有回報出來」——無聲截斷會讓使用者誤以為 AI 讀過全部資料。
/// </summary>
public sealed class ChatContextBuilderTests
{
    #region 基本組裝

    [Fact]
    public void Build_ShouldKeepSourceOrderAndLabelEachSection()
    {
        var result = ChatContextBuilder.Build(
        [
            new ChatSource("會議紀錄：需求確認", "確認了交付範圍。"),
            new ChatSource("附件：合約.pdf", "付款條件為月結三十天。"),
        ]);

        Assert.Equal(["會議紀錄：需求確認", "附件：合約.pdf"], result.UsedLabels);
        Assert.Empty(result.TruncatedLabels);
        Assert.Empty(result.SkippedLabels);

        // 順序就是優先權，脈絡裡也必須維持傳入順序。
        var meetingIndex = result.Context.IndexOf("會議紀錄：需求確認", StringComparison.Ordinal);
        var fileIndex = result.Context.IndexOf("附件：合約.pdf", StringComparison.Ordinal);
        Assert.True(meetingIndex >= 0 && fileIndex > meetingIndex);

        Assert.Contains("付款條件為月結三十天。", result.Context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_SourceWithoutContent_ShouldBeSkippedNotSilentlyDropped(string? content)
    {
        var result = ChatContextBuilder.Build([new ChatSource("附件：掃描檔.pdf", content)]);

        Assert.Empty(result.UsedLabels);
        Assert.Equal(["附件：掃描檔.pdf"], result.SkippedLabels);
        Assert.False(result.HasContent);
    }

    [Fact]
    public void Build_NoSources_ShouldReportNoContent()
    {
        var result = ChatContextBuilder.Build([]);

        Assert.False(result.HasContent);
        Assert.Equal(string.Empty, result.Context);
    }

    #endregion

    #region 預算與截斷

    [Fact]
    public void Build_ShouldNotExceedBudget()
    {
        const int budget = 500;

        var result = ChatContextBuilder.Build(
        [
            new ChatSource("甲", new string('一', 400)),
            new ChatSource("乙", new string('二', 400)),
            new ChatSource("丙", new string('三', 400)),
        ], budget);

        Assert.True(
            result.Context.Length <= budget,
            $"脈絡長度 {result.Context.Length} 超出預算 {budget}。");
    }

    [Fact]
    public void Build_LongSource_ShouldBeTruncatedAndReported()
    {
        var result = ChatContextBuilder.Build(
            [new ChatSource("逐字稿", new string('話', 5000))],
            maxChars: 500);

        Assert.Equal(["逐字稿"], result.UsedLabels);
        Assert.Equal(["逐字稿"], result.TruncatedLabels);

        // 要讓模型知道自己看到的不是全文。
        Assert.Contains("已截斷", result.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EarlierSourcesWin_WhenBudgetRunsOut()
    {
        // 順序即優先權：前面的來源先吃預算，後面的放不下就整份跳過。
        var result = ChatContextBuilder.Build(
        [
            new ChatSource("會議紀錄", new string('甲', 400)),
            new ChatSource("附件", new string('乙', 400)),
        ], maxChars: 300);

        Assert.Equal(["會議紀錄"], result.UsedLabels);
        Assert.Equal(["會議紀錄"], result.TruncatedLabels);
        Assert.Equal(["附件"], result.SkippedLabels);
    }

    [Fact]
    public void Build_ShouldRejectNonPositiveBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChatContextBuilder.Build([new ChatSource("甲", "內容")], maxChars: 0));
    }

    #endregion
}
