using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Tests;

/// <summary>
/// 逐字稿分段（map-reduce 的 map 邊界）的單元測試。
/// 這裡刻意只測純函式——不打任何 LLM、不碰資料庫。
/// </summary>
public sealed class TranscriptChunkerTests
{
    #region 不需分段的情況

    [Fact]
    public void Split_ShouldReturnSingleChunk_WhenTranscriptIsShorterThanLimit()
    {
        var result = TranscriptChunker.Split("短短一段逐字稿。", maxChars: 100);

        Assert.Single(result);
        Assert.Equal("短短一段逐字稿。", result[0]);
    }

    [Fact]
    public void Split_ShouldReturnEmpty_WhenTranscriptIsBlank()
    {
        Assert.Empty(TranscriptChunker.Split(null, maxChars: 100));
        Assert.Empty(TranscriptChunker.Split("   ", maxChars: 100));
    }

    #endregion

    #region 依轉錄的分段邊界切

    [Fact]
    public void Split_ShouldPreferSegmentBoundaries_WhenTranscriptExceedsLimit()
    {
        // 轉錄產出的逐字稿以 "\n\n" 接合每個 15 分鐘分段，優先在這個邊界切開。
        var transcript = string.Join("\n\n", new string('甲', 40), new string('乙', 40), new string('丙', 40));

        var result = TranscriptChunker.Split(transcript, maxChars: 100);

        // 40 + 2 + 40 = 82 可以合併；再加 42 會超過 100，因此第三段自己一塊。
        Assert.Equal(2, result.Count);
        Assert.Equal($"{new string('甲', 40)}\n\n{new string('乙', 40)}", result[0]);
        Assert.Equal(new string('丙', 40), result[1]);
    }

    [Fact]
    public void Split_ShouldNotProduceChunkOverLimit()
    {
        var transcript = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => new string((char)('A' + i), 30)));

        var result = TranscriptChunker.Split(transcript, maxChars: 100);

        Assert.All(result, chunk => Assert.True(
            chunk.Length <= 100,
            $"分段長度 {chunk.Length} 超過上限 100。"));
    }

    #endregion

    #region 單一分段就超長時硬切

    [Fact]
    public void Split_ShouldHardSplit_WhenOneSegmentAloneExceedsLimit()
    {
        var result = TranscriptChunker.Split(new string('字', 250), maxChars: 100);

        Assert.Equal(3, result.Count);
        Assert.Equal(100, result[0].Length);
        Assert.Equal(100, result[1].Length);
        Assert.Equal(50, result[2].Length);
    }

    [Fact]
    public void Split_ShouldPreserveAllContent_WhenHardSplitting()
    {
        var transcript = new string('字', 250);

        var rejoined = string.Concat(TranscriptChunker.Split(transcript, maxChars: 100));

        Assert.Equal(transcript, rejoined);
    }

    #endregion

    #region 參數防呆

    [Fact]
    public void Split_ShouldThrow_WhenMaxCharsIsNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptChunker.Split("內容", maxChars: 0));
    }

    #endregion
}
