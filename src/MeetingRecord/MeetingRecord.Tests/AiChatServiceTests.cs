using MeetingRecord.Business.Services.AiChat;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 問答裡兩段純邏輯的單元測試：對話歷史的修剪與提示詞組裝。
/// 這裡刻意不打真實 API，也不碰資料庫。
/// </summary>
public sealed class AiChatServiceTests
{
    private static AiChatMessageItem User(string content)
        => new(AiChatService.UserRole, content, "小明", DateTime.Now);

    private static AiChatMessageItem Assistant(string content)
        => new(AiChatService.AssistantRole, content, null, DateTime.Now);

    /// <summary>造出 <paramref name="turns"/> 輪一問一答。</summary>
    private static List<AiChatMessageItem> BuildHistory(int turns)
    {
        var history = new List<AiChatMessageItem>();
        for (var index = 1; index <= turns; index++)
        {
            history.Add(User($"問題{index}"));
            history.Add(Assistant($"回答{index}"));
        }

        return history;
    }

    #region 歷史修剪

    [Fact]
    public void TakeRecentHistory_ShorterThanLimit_ShouldReturnEverything()
    {
        var history = BuildHistory(2);

        var recent = AiChatService.TakeRecentHistory(history, maxTurns: 6);

        Assert.Equal(history.Count, recent.Count);
    }

    [Fact]
    public void TakeRecentHistory_ShouldKeepTheMostRecentTurns()
    {
        // 一輪＝一問一答，所以 6 輪是 12 則訊息。
        var history = BuildHistory(10);

        var recent = AiChatService.TakeRecentHistory(history, maxTurns: 6);

        Assert.Equal(12, recent.Count);

        // 留下來的必須是「最後」6 輪，不是最前面的。
        Assert.Equal("問題5", recent[0].Content);
        Assert.Equal("回答10", recent[^1].Content);
    }

    [Fact]
    public void TakeRecentHistory_ZeroTurns_ShouldReturnEmpty()
    {
        var recent = AiChatService.TakeRecentHistory(BuildHistory(3), maxTurns: 0);

        Assert.Empty(recent);
    }

    #endregion

    #region 提示詞組裝

    [Fact]
    public void BuildUserPrompt_ShouldContainContextAndQuestion()
    {
        var prompt = AiChatService.BuildUserPrompt(
            "### 附件：合約.pdf\n付款條件為月結三十天。",
            [],
            "付款條件是什麼？");

        Assert.Contains("付款條件為月結三十天。", prompt, StringComparison.Ordinal);
        Assert.Contains("付款條件是什麼？", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_WithoutHistory_ShouldNotAddHistorySection()
    {
        var prompt = AiChatService.BuildUserPrompt("參考資料內容", [], "問題");

        Assert.DoesNotContain("先前的對話", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_WithHistory_ShouldLabelSpeakers()
    {
        var prompt = AiChatService.BuildUserPrompt(
            "參考資料內容",
            [User("上一個問題"), Assistant("上一個回答")],
            "追問");

        Assert.Contains("先前的對話", prompt, StringComparison.Ordinal);
        Assert.Contains("使用者：上一個問題", prompt, StringComparison.Ordinal);
        Assert.Contains("助理：上一個回答", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_LongHistory_ShouldOnlyCarryRecentTurns()
    {
        // 歷史無上限累積會讓每次提問都比上次更貴，所以只帶最近 MaxHistoryTurns 輪。
        // 10 輪保留最後 6 輪＝問題 5～10，問題 4 是被丟掉的那一側的邊界。
        var prompt = AiChatService.BuildUserPrompt("參考資料內容", BuildHistory(10), "追問");

        // 斷言用 4／5 這組邊界而不是 1／10：「問題10」本身就含有「問題1」，
        // 用 1 去做 DoesNotContain 永遠會失敗。
        Assert.DoesNotContain("問題4", prompt, StringComparison.Ordinal);
        Assert.Contains("問題5", prompt, StringComparison.Ordinal);
        Assert.Contains("問題10", prompt, StringComparison.Ordinal);
    }

    #endregion

    #region 附件支援格式

    [Theory]
    [InlineData("合約.pdf")]
    [InlineData("紀要.docx")]
    [InlineData("備註.txt")]
    [InlineData("說明.md")]
    [InlineData("清單.csv")]
    [InlineData("合約.PDF")]
    public void IsSupported_ShouldAcceptExtractableFormats(string fileName)
    {
        Assert.True(AttachmentTextExtractor.IsSupported(fileName));
    }

    [Theory]
    [InlineData("舊版合約.doc")]
    [InlineData("報價.xlsx")]
    [InlineData("照片.jpg")]
    [InlineData("封存.zip")]
    [InlineData("沒有副檔名")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSupported_ShouldRejectFormatsWithoutTextLayer(string? fileName)
    {
        Assert.False(AttachmentTextExtractor.IsSupported(fileName));
    }

    #endregion
}
