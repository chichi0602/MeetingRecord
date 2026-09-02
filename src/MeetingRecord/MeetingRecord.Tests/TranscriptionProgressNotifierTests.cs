using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Tests;

/// <summary>
/// 轉錄進度通知器的單元測試。只測記憶體狀態與百分比換算——不碰資料庫、不起背景服務。
/// </summary>
public sealed class TranscriptionProgressNotifierTests
{
    #region 百分比換算

    [Fact]
    public void CalculatePercent_Queued_ShouldBeZero()
    {
        Assert.Equal(0, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Queued, 0, 0));
    }

    [Fact]
    public void CalculatePercent_Converting_ShouldReserveFixedSlice()
    {
        // 轉檔沒有進度回呼，只能給固定值——否則長檔在 ffmpeg 期間會一直停在 0%。
        Assert.Equal(
            TranscriptionProgressNotifier.ConvertingPercent,
            TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Converting, 0, 0));
    }

    [Fact]
    public void CalculatePercent_Completed_ShouldBeHundred()
    {
        Assert.Equal(100, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Completed, 3, 8));
    }

    [Theory]
    [InlineData(1, 8, 16)]   // 5 + 95 * 1/8 = 16.875 → 16
    [InlineData(4, 8, 52)]   // 5 + 95 * 4/8 = 52.5   → 52
    [InlineData(8, 8, 100)]  // 5 + 95 * 8/8 = 100
    public void CalculatePercent_Transcribing_ShouldInterpolateBetweenConvertingAndFull(
        int completed, int total, int expected)
    {
        Assert.Equal(expected, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Transcribing, completed, total));
    }

    [Fact]
    public void CalculatePercent_SingleSegmentFile_ShouldJumpFromSliceToFull()
    {
        // 15 分鐘以下的音檔只有一段，所以進度必然是 5% → 100%，中間沒有東西可報。
        Assert.Equal(TranscriptionProgressNotifier.ConvertingPercent,
            TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Converting, 0, 0));
        Assert.Equal(100, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Transcribing, 1, 1));
    }

    [Fact]
    public void CalculatePercent_TranscribingWithoutTotal_ShouldFallBackToConvertingSlice()
    {
        Assert.Equal(
            TranscriptionProgressNotifier.ConvertingPercent,
            TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Transcribing, 0, 0));
    }

    [Fact]
    public void CalculatePercent_ShouldClampCompletedSegments()
    {
        Assert.Equal(100, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Transcribing, 99, 4));
    }

    [Fact]
    public void CalculatePercent_Failed_ShouldKeepProgressAtBreakPoint()
    {
        // 失敗時停在中斷當下的進度，使用者才看得出是哪個階段掛的。
        Assert.Equal(52, TranscriptionProgressNotifier.CalculatePercent(TranscriptionPhase.Failed, 4, 8));
    }

    #endregion

    #region 狀態轉換

    [Fact]
    public void Lifecycle_ShouldMoveThroughQueuedConvertingTranscribingCompleted()
    {
        var notifier = new TranscriptionProgressNotifier();

        notifier.Enqueued(7, "季度檢討會議", teams: null);
        Assert.Equal(TranscriptionPhase.Queued, notifier.Find(7)!.Phase);
        Assert.True(notifier.Find(7)!.IsRunning);

        notifier.ReportConverting(7);
        Assert.Equal(TranscriptionPhase.Converting, notifier.Find(7)!.Phase);

        notifier.ReportSegment(7, 1, 4);
        var transcribing = notifier.Find(7)!;
        Assert.Equal(TranscriptionPhase.Transcribing, transcribing.Phase);
        Assert.Equal(1, transcribing.CompletedSegments);
        Assert.Equal(4, transcribing.TotalSegments);

        notifier.ReportCompleted(7);
        var completed = notifier.Find(7)!;
        Assert.Equal(TranscriptionPhase.Completed, completed.Phase);
        Assert.Equal(100, completed.Percent);
        Assert.False(completed.IsRunning);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public void ReportFailed_ShouldKeepMessageAndStopRunning()
    {
        var notifier = new TranscriptionProgressNotifier();
        notifier.Enqueued(3, "失敗的會議", teams: null);
        notifier.ReportSegment(3, 2, 5);

        notifier.ReportFailed(3, "找不到 ffmpeg。");

        var item = notifier.Find(3)!;
        Assert.Equal(TranscriptionPhase.Failed, item.Phase);
        Assert.Equal("找不到 ffmpeg。", item.ErrorMessage);
        Assert.False(item.IsRunning);
    }

    [Fact]
    public void Enqueued_SameMeetingAgain_ShouldReplacePreviousItem()
    {
        // 重新轉錄會用同一個 Id 再入列一次，不該留下上一輪的完成狀態。
        var notifier = new TranscriptionProgressNotifier();
        notifier.Enqueued(5, "舊標題", teams: null);
        notifier.ReportCompleted(5);

        notifier.Enqueued(5, "新標題", teams: null);

        var item = notifier.Find(5)!;
        Assert.Equal(TranscriptionPhase.Queued, item.Phase);
        Assert.Equal("新標題", item.Title);
        Assert.Equal(0, item.Percent);
        Assert.Null(item.CompletedAt);
        Assert.Single(notifier.GetSnapshot());
    }

    [Fact]
    public void Dismiss_ShouldRemoveFromSnapshot()
    {
        var notifier = new TranscriptionProgressNotifier();
        notifier.Enqueued(1, "甲會議", teams: null);
        notifier.Enqueued(2, "乙會議", teams: null);

        notifier.Dismiss(1);

        Assert.Null(notifier.Find(1));
        Assert.Equal([2], notifier.GetSnapshot().Select(x => x.MeetingId));
    }

    [Fact]
    public void Report_UnknownMeeting_ShouldBeIgnored()
    {
        // 應用程式重啟後才輪到的殘留工作沒有經過 Enqueued，不該憑空生出沒有標題的項目。
        var notifier = new TranscriptionProgressNotifier();

        notifier.ReportConverting(99);
        notifier.ReportSegment(99, 1, 2);
        notifier.ReportCompleted(99);

        Assert.Empty(notifier.GetSnapshot());
    }

    [Fact]
    public void Enqueued_ShouldKeepTeamsForVisibilityFiltering()
    {
        // 面板要靠這個欄位過濾，不然會把受團隊限制的會議標題顯示給無權的人。
        var notifier = new TranscriptionProgressNotifier();

        notifier.Enqueued(4, "機密會議", teams: "\n團隊A\n");

        Assert.Equal("\n團隊A\n", notifier.Find(4)!.Teams);
    }

    #endregion

    #region 變更通知

    [Fact]
    public void Changed_ShouldFireOnEveryStateTransition()
    {
        var notifier = new TranscriptionProgressNotifier();
        var count = 0;
        notifier.Changed += () => count++;

        notifier.Enqueued(1, "會議", teams: null);
        notifier.ReportConverting(1);
        notifier.ReportSegment(1, 1, 2);
        notifier.ReportSegment(1, 2, 2);
        notifier.ReportCompleted(1);
        notifier.Dismiss(1);

        Assert.Equal(6, count);
    }

    [Fact]
    public void Changed_ShouldNotFireForUnknownMeeting()
    {
        var notifier = new TranscriptionProgressNotifier();
        var count = 0;
        notifier.Changed += () => count++;

        notifier.ReportCompleted(12345);

        Assert.Equal(0, count);
    }

    #endregion
}
