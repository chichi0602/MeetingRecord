using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Tests;

/// <summary>
/// 音檔時長解析（0.4.80）。時長是**語音轉錄的計費單位**，解析失敗不是小事：
/// 整場會議的轉錄費用會變成零，而畫面上看起來一切正常。
///
/// <para>
/// 下面的樣本全部取自實跑 FFmpeg 9.0.1 的真實輸出，不是想像出來的格式。
/// </para>
/// </summary>
public sealed class MediaDurationParserTests
{
    #region 真實輸出格式

    [Fact]
    public void TryParseDuration_ShouldParseRealFfmpegOutput()
    {
        // 實測輸出，一字不改。
        const string output = """
            Input #0, wav, from 'C:/Windows/Media/Alarm01.wav':
              Duration: 00:00:05.57, bitrate: 705 kb/s
              Stream #0:0: Audio: pcm_s16le ([1][0][0][0] / 0x0001), 22050 Hz, mono, s16, 705 kb/s
            """;

        var duration = MediaDurationParser.TryParseDuration(output);

        Assert.NotNull(duration);
        Assert.Equal(5.57, duration.Value.TotalSeconds, precision: 2);
    }

    [Fact]
    public void TryParseDuration_ShouldNotRequireStartField()
    {
        // ⚠️ 這一筆守的是實測發現：真實輸出**沒有 start: 欄位**。
        // 寫成 `Duration: (.+?), start:` 的話，所有一般檔案都會解析失敗，
        // 然後靜靜地走進推估分支——沒有任何錯誤訊息。
        var withoutStart = MediaDurationParser.TryParseDuration("  Duration: 00:47:12.34, bitrate: 32 kb/s");
        var withStart = MediaDurationParser.TryParseDuration(
            "  Duration: 00:47:12.34, start: 0.025057, bitrate: 32 kb/s");

        Assert.NotNull(withoutStart);
        Assert.NotNull(withStart);
        Assert.Equal(withStart.Value, withoutStart.Value);
        Assert.Equal(new TimeSpan(0, 0, 47, 12, 340), withoutStart.Value);
    }

    [Fact]
    public void TryParseDuration_ShouldHandleLongRecording()
    {
        var duration = MediaDurationParser.TryParseDuration("Duration: 03:12:45.00, bitrate: 32 kb/s");

        Assert.Equal(new TimeSpan(3, 12, 45), duration);
    }

    #endregion

    #region 拿不到時長時必須回 null，不可回 0

    [Fact]
    public void TryParseDuration_NotAvailable_ShouldReturnNull()
    {
        // Duration: N/A 回 0 的話，整場會議的轉錄費用會變成免費。
        var duration = MediaDurationParser.TryParseDuration("  Duration: N/A, bitrate: N/A");

        Assert.Null(duration);
    }

    [Fact]
    public void TryParseDuration_EmptyOutput_ShouldReturnNull()
    {
        // ⚠️ 空字串正是 `-loglevel error` 的現況（0.4.80 之前）。
        // 萬一有人把 loglevel 改回去，這一筆就是說明書。
        Assert.Null(MediaDurationParser.TryParseDuration(string.Empty));
        Assert.Null(MediaDurationParser.TryParseDuration(null));
        Assert.Null(MediaDurationParser.TryParseDuration("   "));
    }

    #endregion

    #region 分段秒數

    [Fact]
    public void SplitSegmentSeconds_LastSegmentShouldTakeOnlyTheRemainder()
    {
        // 35 分鐘切成 900 秒三段：900 + 900 + 300。
        // 最後一段若也算成 900，轉錄費用會被高估。
        var parts = MediaDurationParser.SplitSegmentSeconds(TimeSpan.FromMinutes(35), segmentCount: 3, segmentSeconds: 900);

        Assert.Equal([900d, 900d, 300d], parts);
        Assert.Equal(2100d, parts.Sum());
    }

    [Fact]
    public void SplitSegmentSeconds_SingleSegment_ShouldTakeWholeDuration()
    {
        var parts = MediaDurationParser.SplitSegmentSeconds(TimeSpan.FromSeconds(42), segmentCount: 1, segmentSeconds: 900);

        Assert.Equal([42d], parts);
    }

    [Fact]
    public void SplitSegmentSeconds_ShouldSumToTotalDuration()
    {
        // 逐段記帳的總和必須等於整段時長，否則帳本與實際用量對不起來。
        var total = TimeSpan.FromSeconds(4321);
        var parts = MediaDurationParser.SplitSegmentSeconds(total, segmentCount: 5, segmentSeconds: 900);

        Assert.Equal(total.TotalSeconds, parts.Sum(), precision: 6);
    }

    [Fact]
    public void SplitSegmentSeconds_ShouldNeverProduceNegativeSeconds()
    {
        // 時長是推估來的時候，段數與時長可能對不上。負秒數會變成負金額，
        // 而負金額在圖表上只是看起來「這個月比較省」。
        var parts = MediaDurationParser.SplitSegmentSeconds(TimeSpan.FromSeconds(100), segmentCount: 4, segmentSeconds: 900);

        Assert.All(parts, x => Assert.True(x >= 0));
        Assert.Equal(100d, parts.Sum(), precision: 6);
    }

    [Fact]
    public void SplitSegmentSeconds_WithoutDuration_ShouldReturnEmpty()
    {
        Assert.Empty(MediaDurationParser.SplitSegmentSeconds(null, segmentCount: 3, segmentSeconds: 900));
    }

    #endregion

    #region 位元組推估

    [Fact]
    public void EstimateSecondsFromBytes_ShouldMatchFixedBitrate()
    {
        // 32kbps 固定位元率：一分鐘 = 32000 * 60 / 8 = 240000 bytes。
        var seconds = MediaDurationParser.EstimateSecondsFromBytes(240_000);

        Assert.NotNull(seconds);
        Assert.Equal(60d, seconds.Value, precision: 6);
    }

    [Fact]
    public void EstimateSecondsFromBytes_ZeroOrNegative_ShouldReturnNull()
    {
        Assert.Null(MediaDurationParser.EstimateSecondsFromBytes(0));
        Assert.Null(MediaDurationParser.EstimateSecondsFromBytes(-1));
    }

    #endregion
}
