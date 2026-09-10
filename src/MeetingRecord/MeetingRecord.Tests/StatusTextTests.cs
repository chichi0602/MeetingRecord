using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 兩個處理狀態列舉的顯示文字測試。
///
/// 重點不只是「已取消」四個字，而是**列舉新增成員時不會有人忘了補顯示文字**——
/// 漏掉時 switch 會落到 `_ => "未知"`，UI 不會出錯、不會警告，只會默默顯示「未知」。
/// </summary>
public sealed class StatusTextTests
{
    [Fact]
    public void TranscriptionStatusText_ShouldDescribeCancelled()
    {
        Assert.Equal("已取消", TranscriptionStatusText.Describe(TranscriptionStatus.Cancelled));
    }

    [Fact]
    public void DraftStatusText_ShouldDescribeCancelled()
    {
        Assert.Equal("已取消", DraftStatusText.Describe(DraftStatus.Cancelled));
    }

    [Fact]
    public void TranscriptionStatusText_ShouldDescribeEveryDefinedValue()
    {
        foreach (var status in Enum.GetValues<TranscriptionStatus>())
        {
            Assert.NotEqual("未知", TranscriptionStatusText.Describe(status));
        }
    }

    [Fact]
    public void DraftStatusText_ShouldDescribeEveryDefinedValue()
    {
        foreach (var status in Enum.GetValues<DraftStatus>())
        {
            Assert.NotEqual("未知", DraftStatusText.Describe(status));
        }
    }

    [Fact]
    public void StatusText_ShouldFallBackForUndefinedValue()
    {
        // 資料庫存的是 int，舊資料或人為改值都可能落在列舉之外，不能讓 UI 拋例外。
        Assert.Equal("未知", TranscriptionStatusText.Describe((TranscriptionStatus)99));
        Assert.Equal("未知", DraftStatusText.Describe((DraftStatus)99));
    }
}
