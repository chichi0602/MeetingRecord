using MeetingRecord.Business.Services.TextGeneration;

namespace MeetingRecord.Tests;

/// <summary>
/// 會議紀錄草稿生成進度通知器的單元測試。只測記憶體狀態與百分比換算。
/// </summary>
public sealed class MeetingDraftProgressNotifierTests
{
    #region 百分比換算

    [Fact]
    public void CalculatePercent_Queued_ShouldBeZero()
    {
        Assert.Equal(0, MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Queued, 0, 0));
    }

    [Fact]
    public void CalculatePercent_Preparing_ShouldReserveFixedSlice()
    {
        Assert.Equal(
            MeetingDraftProgressNotifier.PreparingPercent,
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Preparing, 0, 0));
    }

    [Fact]
    public void CalculatePercent_Generating_ShouldSitAtGeneratingSlice()
    {
        Assert.Equal(
            MeetingDraftProgressNotifier.GeneratingPercent,
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Generating, 0, 0));
    }

    [Fact]
    public void CalculatePercent_Completed_ShouldBeHundred()
    {
        Assert.Equal(100, MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Completed, 2, 4));
    }

    [Theory]
    [InlineData(1, 4, 21)]  // 5 + 65 * 1/4 = 21.25 → 21
    [InlineData(2, 4, 37)]  // 5 + 65 * 2/4 = 37.5  → 37
    [InlineData(4, 4, 70)]  // 5 + 65 * 4/4 = 70，剛好接上 Generating
    public void CalculatePercent_Summarizing_ShouldInterpolateBetweenPreparingAndGenerating(
        int completed, int total, int expected)
    {
        Assert.Equal(expected, MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Summarizing, completed, total));
    }

    [Fact]
    public void CalculatePercent_SummarizingWithoutTotal_ShouldFallBackToPreparingSlice()
    {
        Assert.Equal(
            MeetingDraftProgressNotifier.PreparingPercent,
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Summarizing, 0, 0));
    }

    [Fact]
    public void CalculatePercent_ShouldClampCompletedChunks()
    {
        Assert.Equal(
            MeetingDraftProgressNotifier.GeneratingPercent,
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Summarizing, 99, 3));
    }

    [Fact]
    public void CalculatePercent_Failed_ShouldKeepProgressAtBreakPoint()
    {
        Assert.Equal(37, MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Failed, 2, 4));
    }

    [Fact]
    public void CalculatePercent_FailedBeforeChunking_ShouldSitAtPreparingSlice()
    {
        Assert.Equal(
            MeetingDraftProgressNotifier.PreparingPercent,
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Failed, 0, 0));
    }

    #endregion

    #region 狀態轉換

    [Fact]
    public void Lifecycle_ShortTranscript_ShouldSkipSummarizing()
    {
        // 逐字稿在 12000 字元以內時不做 map-reduce，多數會議都走這條：0 → 5 → 70 → 100。
        var notifier = new MeetingDraftProgressNotifier();

        notifier.Enqueued(7, "需求確認會議", teams: null);
        Assert.Equal(0, notifier.Find(7)!.Percent);

        notifier.ReportPreparing(7);
        Assert.Equal(MeetingDraftProgressNotifier.PreparingPercent, notifier.Find(7)!.Percent);

        notifier.ReportGenerating(7);
        Assert.Equal(MeetingDraftProgressNotifier.GeneratingPercent, notifier.Find(7)!.Percent);

        notifier.ReportCompleted(7);
        var completed = notifier.Find(7)!;
        Assert.Equal(100, completed.Percent);
        Assert.False(completed.IsRunning);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public void Lifecycle_LongTranscript_ShouldMoveThroughSummarizing()
    {
        var notifier = new MeetingDraftProgressNotifier();

        notifier.Enqueued(9, "季度檢討會議", teams: null);
        notifier.ReportPreparing(9);
        notifier.ReportSummarizing(9, 0, 4);
        Assert.Equal(MeetingDraftPhase.Summarizing, notifier.Find(9)!.Phase);

        notifier.ReportSummarizing(9, 2, 4);
        var midway = notifier.Find(9)!;
        Assert.Equal(2, midway.CompletedChunks);
        Assert.Equal(4, midway.TotalChunks);
        Assert.True(midway.IsRunning);

        notifier.ReportGenerating(9);
        notifier.ReportCompleted(9);
        Assert.Equal(100, notifier.Find(9)!.Percent);
    }

    [Fact]
    public void ReportFailed_ShouldKeepMessageAndStopRunning()
    {
        var notifier = new MeetingDraftProgressNotifier();
        notifier.Enqueued(3, "失敗的會議", teams: null);
        notifier.ReportPreparing(3);

        notifier.ReportFailed(3, "找不到逐字稿內容。");

        var item = notifier.Find(3)!;
        Assert.Equal(MeetingDraftPhase.Failed, item.Phase);
        Assert.Equal("找不到逐字稿內容。", item.ErrorMessage);
        Assert.False(item.IsRunning);
    }

    [Fact]
    public void Enqueued_SameMeetingAgain_ShouldReplacePreviousItem()
    {
        // 重新產生會用同一個 Id 再入列一次，不該留下上一輪的完成狀態。
        var notifier = new MeetingDraftProgressNotifier();
        notifier.Enqueued(5, "舊標題", teams: null);
        notifier.ReportCompleted(5);

        notifier.Enqueued(5, "新標題", teams: null);

        var item = notifier.Find(5)!;
        Assert.Equal(MeetingDraftPhase.Queued, item.Phase);
        Assert.Equal("新標題", item.Title);
        Assert.Equal(0, item.Percent);
        Assert.Null(item.CompletedAt);
        Assert.Single(notifier.GetSnapshot());
    }

    [Fact]
    public void Dismiss_ShouldRemoveFromSnapshot()
    {
        var notifier = new MeetingDraftProgressNotifier();
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
        var notifier = new MeetingDraftProgressNotifier();

        notifier.ReportPreparing(99);
        notifier.ReportSummarizing(99, 1, 2);
        notifier.ReportGenerating(99);
        notifier.ReportCompleted(99);

        Assert.Empty(notifier.GetSnapshot());
    }

    [Fact]
    public void Enqueued_ShouldKeepTeamsForVisibilityFiltering()
    {
        // 面板要靠這個欄位過濾，不然會把受團隊限制的會議標題顯示給無權的人。
        var notifier = new MeetingDraftProgressNotifier();

        notifier.Enqueued(4, "機密會議", teams: "\n團隊A\n");

        Assert.Equal("\n團隊A\n", notifier.Find(4)!.Teams);
    }

    #endregion

    #region 變更通知

    [Fact]
    public void Changed_ShouldFireOnEveryStateTransition()
    {
        var notifier = new MeetingDraftProgressNotifier();
        var count = 0;
        notifier.Changed += () => count++;

        notifier.Enqueued(1, "會議", teams: null);
        notifier.ReportPreparing(1);
        notifier.ReportSummarizing(1, 1, 2);
        notifier.ReportGenerating(1);
        notifier.ReportCompleted(1);
        notifier.Dismiss(1);

        Assert.Equal(6, count);
    }

    [Fact]
    public void Changed_ShouldNotFireForUnknownMeeting()
    {
        var notifier = new MeetingDraftProgressNotifier();
        var count = 0;
        notifier.Changed += () => count++;

        notifier.ReportCompleted(12345);

        Assert.Equal(0, count);
    }

    #endregion

    #region 串流字數推動的生成進度

    [Fact]
    public void CalculatePercent_Generating_ShouldRiseWithCharactersAndNeverReachHundred()
    {
        var previous = MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Generating, 0, 0, 0);
        Assert.Equal(MeetingDraftProgressNotifier.GeneratingPercent, previous);

        foreach (var characters in new[] { 100, 500, 900, 2000, 5000, 50000, int.MaxValue })
        {
            var current = MeetingDraftProgressNotifier.CalculatePercent(
                MeetingDraftPhase.Generating, 0, 0, characters);

            Assert.True(current >= previous, $"進度必須單調遞增，{characters} 字時卻從 {previous} 掉到 {current}。");

            // 100% 只有 Completed 給得出來，否則會出現「先衝到 100 再乾等」的失信畫面。
            Assert.True(current <= MeetingDraftProgressNotifier.MaxGeneratingPercent);

            previous = current;
        }

        // 極大字數要真的貼近上限，不能因為整數運算或溢位而卡在起點。
        Assert.InRange(
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Generating, 0, 0, int.MaxValue),
            MeetingDraftProgressNotifier.MaxGeneratingPercent - 1,
            MeetingDraftProgressNotifier.MaxGeneratingPercent);
    }

    [Fact]
    public void CalculatePercent_Generating_HalfLifeCharacters_ShouldReachHalfOfTheSpan()
    {
        var expected = MeetingDraftProgressNotifier.GeneratingPercent
            + ((MeetingDraftProgressNotifier.MaxGeneratingPercent
                - MeetingDraftProgressNotifier.GeneratingPercent) / 2);

        Assert.Equal(
            expected,
            MeetingDraftProgressNotifier.CalculatePercent(
                MeetingDraftPhase.Generating,
                0,
                0,
                MeetingDraftProgressNotifier.GeneratingCharacterHalfLife));
    }

    [Fact]
    public void CalculatePercent_Failed_DuringGenerating_ShouldKeepCharacterProgress()
    {
        // 失敗時停在中斷當下的進度，才看得出是生成到一半掛的，而不是根本沒開始。
        Assert.Equal(
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Generating, 0, 0, 1800),
            MeetingDraftProgressNotifier.CalculatePercent(MeetingDraftPhase.Failed, 0, 0, 1800));
    }

    [Fact]
    public void ReportGeneratedCharacters_ShouldNotifyOnlyWhenPercentChanges()
    {
        // 每個 SSE 片段只有幾個字，逐片通知會用上百次重繪轟炸 Blazor circuit。
        var notifier = new MeetingDraftProgressNotifier();
        notifier.Enqueued(5, "需求確認會議", teams: null);
        notifier.ReportGenerating(5);

        var count = 0;
        notifier.Changed += () => count++;

        notifier.ReportGeneratedCharacters(5, 1);
        notifier.ReportGeneratedCharacters(5, 2);
        notifier.ReportGeneratedCharacters(5, 3);

        // 字數確實更新了，但這三次都還換算成同一個百分比，所以一次事件都不該發。
        Assert.Equal(3, notifier.Find(5)!.GeneratedCharacters);
        Assert.Equal(0, count);

        notifier.ReportGeneratedCharacters(5, 2000);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReportGeneratedCharacters_ShouldIgnoreUnknownMeeting()
    {
        var notifier = new MeetingDraftProgressNotifier();
        var count = 0;
        notifier.Changed += () => count++;

        notifier.ReportGeneratedCharacters(12345, 500);

        Assert.Null(notifier.Find(12345));
        Assert.Equal(0, count);
    }

    [Fact]
    public void ReportGenerating_ShouldResetCharactersFromPreviousRun()
    {
        // 重新產生時若沿用上一輪的字數，進度會從高檔往下掉。
        var notifier = new MeetingDraftProgressNotifier();
        notifier.Enqueued(8, "季度檢討會議", teams: null);
        notifier.ReportGenerating(8);
        notifier.ReportGeneratedCharacters(8, 3000);

        notifier.ReportGenerating(8);

        var item = notifier.Find(8)!;
        Assert.Equal(0, item.GeneratedCharacters);
        Assert.Equal(MeetingDraftProgressNotifier.GeneratingPercent, item.Percent);
    }

    #endregion
}
