using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 例外分析的使用者訊息組裝。
///
/// ⚠️ 個資紅線：帳號與 UserId <b>不可</b>送給外部 AI，只能送「有／無登入使用者」。
/// 以哨兵字串斷言，日後有人「順手把整個物件序列化送出去」會立刻被擋下。
/// </summary>
public sealed class AiExceptionPromptBuilderTests
{
    private const string SentinelAccount = "SENTINEL-ACCOUNT-7f3a";
    private const int SentinelUserId = 987654;
    private const string SentinelSignature = "SENTINEL-SIGNATURE-c0ffee";
    private const string SentinelFile = "202609/SENTINELFILE.txt";

    [Fact]
    public void Build_ShouldIncludeEveryDetailFieldAndStackTrace()
    {
        var message = AiExceptionPromptBuilder.Build(CreateItem(), "   at Foo.Bar() in Foo.cs:line 42");

        Assert.Contains("System.InvalidOperationException", message);
        Assert.Contains("Sequence contains no elements", message);
        Assert.Contains("畫面", message);
        Assert.Contains("/orders", message);
        Assert.Contains("Failed to load {Name}", message);
        Assert.Contains("MeetingRecord.Web.Orders", message);
        Assert.Contains("1,234", message);
        Assert.Contains("2026-09-01 08:00:00", message);
        Assert.Contains("2026-09-27 09:30:00", message);
        Assert.Contains("at Foo.Bar() in Foo.cs:line 42", message);
    }

    [Fact]
    public void Build_ShouldNeverIncludeAccountUserIdOrInternalKeys()
    {
        var message = AiExceptionPromptBuilder.Build(CreateItem(), "stack");

        Assert.DoesNotContain(SentinelAccount, message);
        Assert.DoesNotContain(SentinelUserId.ToString(), message);
        Assert.DoesNotContain(SentinelSignature, message);
        Assert.DoesNotContain("SENTINELFILE", message);
        Assert.Contains("有登入使用者", message);
    }

    [Fact]
    public void Build_ShouldSayAnonymous_WhenNoUser()
    {
        var item = CreateItem();
        item.Account = null;
        item.UserId = null;

        var message = AiExceptionPromptBuilder.Build(item, "stack");

        Assert.Contains("無登入使用者", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Build_ShouldStateStackUnavailable_WhenMissing(string? stackTrace)
    {
        var message = AiExceptionPromptBuilder.Build(CreateItem(), stackTrace);

        Assert.Contains("堆疊不可得", message);
    }

    [Fact]
    public void Build_ShouldUseLfLineEndingsOnly()
    {
        var message = AiExceptionPromptBuilder.Build(CreateItem(), "line1\r\nline2\rline3");

        Assert.DoesNotContain('\r', message);
        Assert.Contains("line1\nline2\nline3", message);
    }

    private static ExceptionLogAdapterModel CreateItem() => new()
    {
        Id = 5,
        Signature = SentinelSignature,
        ExceptionType = "System.InvalidOperationException",
        Message = "Sequence contains no elements",
        Source = "畫面",
        Page = "/orders",
        Operation = "Failed to load {Name}",
        LoggerName = "MeetingRecord.Web.Orders",
        Account = SentinelAccount,
        UserId = SentinelUserId,
        StackTraceFile = SentinelFile,
        OccurrenceCount = 1234,
        FirstOccurredAt = new DateTime(2026, 9, 1, 8, 0, 0),
        LastOccurredAt = new DateTime(2026, 9, 27, 9, 30, 0),
    };
}
