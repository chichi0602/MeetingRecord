using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Web.Components.Views.Monitoring;

namespace MeetingRecord.Tests;

/// <summary>
/// 系統例外紀錄「點列複製」與「複製目前查詢結果」的文字組裝。
///
/// 與 AI 分析不同，這份文字是管理員自己要貼給開發人員的，因此<b>含</b>帳號與 UserId（同明細窗）。
/// </summary>
public sealed class ExceptionLogClipboardTextTests
{
    [Fact]
    public void Build_ShouldListDetailFieldsInDetailWindowOrder()
    {
        var text = ExceptionLogClipboardText.Build(CreateItem(), "stack");

        var expected = string.Join('\n',
            "例外類型：System.InvalidOperationException",
            "訊息：Sequence contains no elements",
            "來源：畫面",
            "頁面：/orders",
            "操作（日誌訊息樣板）：Failed to load {Name}",
            "記錄器：MeetingRecord.Web.Orders",
            "使用者：alice（UserId=7）",
            "累計次數：1,234",
            "首次發生：2026-09-01 08:00:00",
            "最後發生：2026-09-27 09:30:00",
            "最後追蹤碼：K7Q2M9XA");
        Assert.StartsWith(expected + "\n", text);
    }

    [Fact]
    public void Build_ShouldWriteDash_WhenOptionalFieldsAreMissing()
    {
        var item = CreateItem();
        item.Page = null;
        item.Operation = null;
        item.LoggerName = null;
        item.Account = null;
        item.UserId = null;
        item.LastTraceId = null;

        var text = ExceptionLogClipboardText.Build(item, "stack");

        Assert.Contains("最後追蹤碼：—\n", text);
        Assert.Contains("頁面：—\n", text);
        Assert.Contains("操作（日誌訊息樣板）：—\n", text);
        Assert.Contains("記錄器：—\n", text);
        Assert.Contains("使用者：—\n", text);
    }

    [Theory]
    [InlineData("alice", 7, "alice（UserId=7）")]
    [InlineData("alice", null, "alice")]
    [InlineData(null, 7, "—（UserId=7）")]
    [InlineData("  ", null, "—")]
    public void FormatAccount_ShouldMatchDetailWindow(string? account, int? userId, string expected)
    {
        var item = CreateItem();
        item.Account = account;
        item.UserId = userId;

        Assert.Equal(expected, ExceptionLogClipboardText.FormatAccount(item));
    }

    [Fact]
    public void Build_ShouldWrapStackTraceWithMarkers()
    {
        var text = ExceptionLogClipboardText.Build(CreateItem(), "   at Foo.Bar() in Foo.cs:line 42\n");

        Assert.EndsWith(
            "\n\n----- 完整堆疊開始 -----\n   at Foo.Bar() in Foo.cs:line 42\n----- 完整堆疊結束 -----\n",
            text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Build_ShouldExplain_WhenStackTraceIsMissing(string? stackTrace)
    {
        var text = ExceptionLogClipboardText.Build(CreateItem(), stackTrace);

        Assert.EndsWith("\n\n完整堆疊：堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。\n", text);
        Assert.DoesNotContain("完整堆疊開始", text);
    }

    [Fact]
    public void Build_ShouldUseLfLineEndingsOnly()
    {
        var item = CreateItem();
        item.Message = "first\r\nsecond";

        var text = ExceptionLogClipboardText.Build(item, "line1\r\nline2\rline3");

        Assert.DoesNotContain('\r', text);
        Assert.Contains("first\nsecond", text);
        Assert.Contains("line1\nline2\nline3", text);
    }

    [Fact]
    public void BuildMany_ShouldJoinEachRecordWithSeparator()
    {
        var first = CreateItem();
        var second = CreateItem();
        second.ExceptionType = "System.TimeoutException";

        var text = ExceptionLogClipboardText.BuildMany([(first, "stack-1"), (second, null)]);

        var expected = ExceptionLogClipboardText.Build(first, "stack-1")
            + "\n" + new string('=', 50) + "\n\n"
            + ExceptionLogClipboardText.Build(second, null);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void BuildMany_ShouldReturnSingleRecordUnchanged()
    {
        var item = CreateItem();

        Assert.Equal(
            ExceptionLogClipboardText.Build(item, "stack"),
            ExceptionLogClipboardText.BuildMany([(item, "stack")]));
    }

    private static ExceptionLogAdapterModel CreateItem() => new()
    {
        Id = 5,
        Signature = "signature",
        ExceptionType = "System.InvalidOperationException",
        Message = "Sequence contains no elements",
        Source = "畫面",
        Page = "/orders",
        Operation = "Failed to load {Name}",
        LoggerName = "MeetingRecord.Web.Orders",
        Account = "alice",
        UserId = 7,
        StackTraceFile = "202609/file.txt",
        OccurrenceCount = 1234,
        FirstOccurredAt = new DateTime(2026, 9, 1, 8, 0, 0),
        LastOccurredAt = new DateTime(2026, 9, 27, 9, 30, 0),
        LastTraceId = "K7Q2M9XA",
    };
}
