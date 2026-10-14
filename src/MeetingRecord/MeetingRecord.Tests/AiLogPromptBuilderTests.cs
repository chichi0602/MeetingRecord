using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// Prompt 組裝測試。
///
/// 0.9.7 起 <see cref="AiLogPromptBuilder.Build"/> 只剩一道處理：取最新 N 筆。
/// 字元層級的截斷全部移除了，所以這裡最重要的一支是
/// <c>Build_ShouldSendEveryCharacter_WhenWithinEntryLimit</c> —— 它釘住「有多少字就送多少字」
/// 這個承諾。
/// </summary>
public sealed class AiLogPromptBuilderTests
{
    private const int DefaultMaxEntries = 100;

    [Fact]
    public void Build_ShouldReturnEmpty_WhenNoEntries()
    {
        var result = AiLogPromptBuilder.Build([], DefaultMaxEntries);

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.IncludedEntryCount);
        Assert.Equal(string.Empty, result.UserMessage);
    }

    [Fact]
    public void Build_ShouldReturnEmpty_WhenNull()
    {
        var result = AiLogPromptBuilder.Build(null, DefaultMaxEntries);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Build_ShouldKeepNewestEntries_WhenExceedingMaxEntries()
    {
        var entries = CreateEntries(10);

        var result = AiLogPromptBuilder.Build(entries, maxEntries: 3);

        Assert.Equal(10, result.TotalEntryCount);
        Assert.Equal(3, result.IncludedEntryCount);
        Assert.True(result.DroppedByEntryLimit);

        // 最新三筆是 7、8、9；最舊的 0 不該出現。
        Assert.Contains("raw-7", result.UserMessage);
        Assert.Contains("raw-9", result.UserMessage);
        Assert.DoesNotContain("raw-0", result.UserMessage);
    }

    /// <summary>送出的內容必須保持時間正序，模型才讀得出事件先後。</summary>
    [Fact]
    public void Build_ShouldPreserveAscendingOrderInOutput()
    {
        var entries = CreateEntries(5);

        var message = AiLogPromptBuilder.Build(entries, maxEntries: 3).UserMessage;

        Assert.True(message.IndexOf("raw-2", StringComparison.Ordinal)
            < message.IndexOf("raw-3", StringComparison.Ordinal));
        Assert.True(message.IndexOf("raw-3", StringComparison.Ordinal)
            < message.IndexOf("raw-4", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠️ 本次決策的核心承諾：有多少字就送多少字。
    ///
    /// 0.9.7 移除了兩道字元截斷（每筆固定長度、總量預算），理由是它們會把堆疊的尾巴切掉，
    /// 而尾巴常常才是根因所在。這支測試用一筆 50000 字元的日誌驗證內容一個字都沒少。
    /// 若有人日後想加回任何字元上限，這裡會先紅。
    /// </summary>
    [Fact]
    public void Build_ShouldSendEveryCharacter_WhenWithinEntryLimit()
    {
        var hugeRaw = new string('x', 50000);
        var entries = new List<LogEntry>
        {
            new() { Sequence = 1, Raw = hugeRaw },
            new() { Sequence = 2, Raw = "short" },
        };

        var result = AiLogPromptBuilder.Build(entries, DefaultMaxEntries);

        Assert.Equal(2, result.IncludedEntryCount);
        Assert.False(result.DroppedByEntryLimit);
        Assert.Contains(hugeRaw, result.UserMessage);
        Assert.Contains("short", result.UserMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Build_ShouldClampNonPositiveEntryLimit(int maxEntries)
    {
        var entries = CreateEntries(5);

        var result = AiLogPromptBuilder.Build(entries, maxEntries);

        // 夾住而非丟例外：設定手誤不該讓整個功能掛掉。
        Assert.False(result.IsEmpty);
        Assert.True(result.IncludedEntryCount >= 1);
    }

    [Fact]
    public void Build_ShouldNormalizeCrLfToLf()
    {
        var entries = new List<LogEntry> { new() { Sequence = 1, Raw = "第一行\r\n第二行\r第三行" } };

        var message = AiLogPromptBuilder.Build(entries, DefaultMaxEntries).UserMessage;

        Assert.DoesNotContain("\r", message);
        Assert.Contains("第一行\n第二行\n第三行", message);
    }

    /// <summary>剛好等於筆數上限時不算捨棄，說明行也不該出現。</summary>
    [Fact]
    public void Build_ShouldNotReportDropped_WhenExactlyAtEntryLimit()
    {
        var entries = CreateEntries(5);

        var result = AiLogPromptBuilder.Build(entries, maxEntries: 5);

        Assert.False(result.DroppedByEntryLimit);
        Assert.Equal(5, result.IncludedEntryCount);
        Assert.DoesNotContain("原始查詢共", result.UserMessage);
    }

    [Fact]
    public void Build_ShouldStateTruncationInHeader()
    {
        var entries = CreateEntries(10);

        var message = AiLogPromptBuilder.Build(entries, maxEntries: 3).UserMessage;

        Assert.Contains("原始查詢共 10 筆", message);
        Assert.Contains("僅送出最新 3 筆", message);
    }

    [Fact]
    public void Build_ShouldSeparateEntries()
    {
        var entries = CreateEntries(3);

        var message = AiLogPromptBuilder.Build(entries, DefaultMaxEntries).UserMessage;

        var separatorCount = message.Split('\n').Count(line => line == AiLogPromptBuilder.EntrySeparator);
        Assert.Equal(3, separatorCount);
    }

    private static List<LogEntry> CreateEntries(int count, int rawLength = 0)
    {
        var entries = new List<LogEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var raw = $"raw-{index} ";
            if (rawLength > raw.Length)
            {
                raw += new string('z', rawLength - raw.Length);
            }

            entries.Add(new LogEntry
            {
                Sequence = index + 1,
                Timestamp = new DateTime(2026, 9, 11, 10, 0, 0).AddMinutes(index),
                Level = "INFO",
                Raw = raw,
            });
        }

        return entries;
    }
}
