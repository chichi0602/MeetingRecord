using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Tests;

/// <summary>
/// 切在停頓處與截斷判斷（1.1.61）。全部是純函式，不跑 FFmpeg。
/// </summary>
public sealed class TranscriptionSegmentPlannerTests
{
    /// <summary>
    /// 實際 FFmpeg 9.0.1 的輸出：15 分鐘的測試音檔，在 100、270（2.5 秒）、290（0.6 秒）、520、610、850 秒各有一段安靜。
    /// </summary>
    private const string RealSilenceDetectOutput = """
          Duration: 00:15:00.00, start: 0.069063, bitrate: 32 kb/s
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 99.999938
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 101.064 | silence_duration: 1.064062
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 269.999937
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 272.512 | silence_duration: 2.512062
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 289.999937
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 290.64 | silence_duration: 0.640062
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 519.999937
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 520.832 | silence_duration: 0.832063
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 609.999937
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 611.512 | silence_duration: 1.512063
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_start: 849.999937
        [Parsed_silencedetect_0 @ 000001b44d03cac0] silence_end: 851.064 | silence_duration: 1.064062
        """;

    [Fact]
    public void ParseSilences_RealFfmpegOutput_ShouldReturnEverySpan()
    {
        var silences = TranscriptionSegmentPlanner.ParseSilences(RealSilenceDetectOutput);

        Assert.Equal(6, silences.Count);
        Assert.Equal(269.999937, silences[1].Start, 6);
        Assert.Equal(272.512, silences[1].End, 6);
    }

    [Fact]
    public void ParseSilences_TrailingStartWithoutEnd_ShouldBeIgnored()
    {
        // 音檔結尾的安靜只有 silence_start，切在那裡沒有意義。
        var silences = TranscriptionSegmentPlanner.ParseSilences("""
            [silencedetect @ 0x1] silence_start: 10
            [silencedetect @ 0x1] silence_end: 11 | silence_duration: 1
            [silencedetect @ 0x1] silence_start: 58.5
            """);

        Assert.Equal([new SilenceSpan(10, 11)], silences);
    }

    [Fact]
    public void PlanCutPoints_RealOutput_ShouldCutAtLongestPauseInsideEachWindow()
    {
        var silences = TranscriptionSegmentPlanner.ParseSilences(RealSilenceDetectOutput);

        var cuts = TranscriptionSegmentPlanner.PlanCutPoints(900, silences, maxSegmentSeconds: 300);

        // 第一段：240～300 秒內有 270（2.5 秒）與 290（0.6 秒）兩個停頓，取比較長的 270。
        // 第二段：511～571 秒內只有 520。
        // 第三段：760～820 秒內沒有停頓（850 在視窗外），退回整整 300 秒。
        Assert.Equal([271.256, 520.416, 820.416], cuts);
    }

    [Fact]
    public void PlanCutPoints_NoSilence_ShouldFallBackToFixedLength()
    {
        var cuts = TranscriptionSegmentPlanner.PlanCutPoints(1000, [], maxSegmentSeconds: 300);

        Assert.Equal([300d, 600d, 900d], cuts);
    }

    [Fact]
    public void PlanCutPoints_ShortMedia_ShouldNotCut()
    {
        Assert.Empty(TranscriptionSegmentPlanner.PlanCutPoints(300, [new SilenceSpan(100, 101)], maxSegmentSeconds: 300));
        Assert.Empty(TranscriptionSegmentPlanner.PlanCutPoints(0, [], maxSegmentSeconds: 300));
    }

    [Fact]
    public void PlanCutPoints_SegmentsShouldNeverExceedTheLimit()
    {
        // ⚠️ 上限是轉錄模型的輸出長度，切在停頓處只能讓段落變短、不能變長。
        var silences = Enumerable.Range(1, 400).Select(i => new SilenceSpan(i * 9.7, i * 9.7 + 0.5)).ToList();

        var cuts = TranscriptionSegmentPlanner.PlanCutPoints(3600, silences, maxSegmentSeconds: 300);
        var durations = TranscriptionSegmentPlanner.SegmentDurations(3600, cuts);

        Assert.All(durations, seconds => Assert.InRange(seconds, 0.001, 300));
        Assert.Equal(3600, durations.Sum(), 6);
    }

    [Fact]
    public void PickSplitPoint_ShouldPreferPauseNearTheMiddle()
    {
        var silences = new List<SilenceSpan> { new(20, 23), new(140, 141), new(160, 160.5), new(280, 285) };

        // 中間 35%～65%（105～195 秒）內取最長的 140～141；20 與 280 雖然更長但離中間太遠。
        Assert.Equal(140.5, TranscriptionSegmentPlanner.PickSplitPoint(300, silences));
    }

    [Fact]
    public void PickSplitPoint_NoPauseNearTheMiddle_ShouldUseExactMiddle()
    {
        Assert.Equal(150, TranscriptionSegmentPlanner.PickSplitPoint(300, [new SilenceSpan(10, 20)]));
    }

    [Fact]
    public void SegmentDurations_ShouldSumToTotal()
    {
        var durations = TranscriptionSegmentPlanner.SegmentDurations(900, [271.256, 520.416, 820.416]);

        Assert.Equal(4, durations.Count);
        Assert.Equal(271.256, durations[0], 6);
        Assert.Equal(79.584, durations[3], 6);
        Assert.Equal(900, durations.Sum(), 6);
    }

    [Fact]
    public void LooksTruncated_ShouldDependOnEstimatedTokensNotCharacters()
    {
        // 5 分鐘講話最密約 1500 字，不應觸發；被截斷的段落實測停在 1800～2400 字。
        Assert.False(TranscriptionSegmentPlanner.LooksTruncated(new string('會', 1500)));
        Assert.True(TranscriptionSegmentPlanner.LooksTruncated(new string('會', 1800)));

        // 英文約四個字元一個 token：2000 個字元只有約 500 token，離上限很遠。
        Assert.False(TranscriptionSegmentPlanner.LooksTruncated(string.Concat(Enumerable.Repeat("word ", 400))));
        Assert.False(TranscriptionSegmentPlanner.LooksTruncated(null));
    }

    [Fact]
    public void BuildSegmentArguments_WithCutPoints_ShouldUseSegmentTimes()
    {
        var arguments = FfmpegMediaConverter.BuildSegmentArguments(
            @"C:\media\meeting.mp3",
            @"C:\temp\part_%04d.mp3",
            300,
            [271.256, 520.416]);

        Assert.Contains("-segment_times 271.256,520.416", arguments);
        Assert.DoesNotContain("-segment_time ", arguments);
    }

    [Fact]
    public void BuildSegmentArguments_WithoutCutPoints_ShouldKeepFixedLength()
    {
        var arguments = FfmpegMediaConverter.BuildSegmentArguments(@"C:\a.mp3", @"C:\part_%04d.mp3", 300, []);

        Assert.Contains("-segment_time 300", arguments);
    }

    [Fact]
    public void BuildSilenceDetectArguments_ShouldOnlyAnalyzeAndKeepInfoLogLevel()
    {
        var arguments = FfmpegMediaConverter.BuildSilenceDetectArguments(@"C:\my media\會議.mp4");

        Assert.Contains("-af silencedetect=noise=-30dB:d=0.4", arguments);
        Assert.Contains("-loglevel info", arguments);
        Assert.EndsWith("-f null -", arguments);
        Assert.Contains("\"C:\\my media\\會議.mp4\"", arguments);
    }

    [Fact]
    public void BuildCopySplitArguments_ShouldNotReencode()
    {
        var arguments = FfmpegMediaConverter.BuildCopySplitArguments(@"C:\part_0001.mp3", @"C:\part_0001_half_%d.mp3", 140.5);

        Assert.Contains("-c copy", arguments);
        Assert.Contains("-segment_times 140.5", arguments);
    }
}
