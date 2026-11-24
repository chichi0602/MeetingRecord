using MeetingRecord.Business.Services.Diagnostics;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.1.72 轉錄比較（管理者）：條件檢查、英文占比、計費單位、剪片段的 FFmpeg 參數。
/// 請求表單（語言、提示詞、換部署）在 <see cref="DiarizedTranscriptionTests"/>。
/// </summary>
public sealed class TranscriptionComparisonTests
{
    [Fact]
    public void LatinRatio_ShouldCountLettersOnly()
    {
        // 「奇怪的英文」量化：中文字也算字母，空白、標點不算。
        Assert.Equal(0, TranscriptionComparisonService.LatinRatio("我們今天討論上線時程。"));
        Assert.Equal(0.5, TranscriptionComparisonService.LatinRatio("上線 OK，好的 ab"), precision: 3);
        Assert.Equal(1, TranscriptionComparisonService.LatinRatio("Hello world"));
        Assert.Equal(0, TranscriptionComparisonService.LatinRatio(null));
        Assert.Equal(0, TranscriptionComparisonService.LatinRatio("，。！"));
    }

    [Fact]
    public void Validate_ShouldRejectOutOfRangeRequests()
    {
        TranscriptionCompareRequestDto Request(int seconds, int variants, int start = 0) => new()
        {
            MeetingId = 1,
            StartSeconds = start,
            Seconds = seconds,
            Variants = [.. Enumerable.Range(0, variants).Select(_ => new TranscriptionCompareVariantDto())],
        };

        Assert.Null(TranscriptionComparisonService.Validate(Request(300, 4)));
        Assert.NotNull(TranscriptionComparisonService.Validate(Request(300, 0)));
        Assert.NotNull(TranscriptionComparisonService.Validate(Request(300, 5)));
        Assert.NotNull(TranscriptionComparisonService.Validate(Request(10, 1)));
        Assert.NotNull(TranscriptionComparisonService.Validate(Request(301, 1)));
        Assert.NotNull(TranscriptionComparisonService.Validate(Request(60, 1, start: -1)));
    }

    [Fact]
    public void Feature_ShouldBeBilledByAudioDuration()
    {
        // 漏掉的話費用會用 token 算，而轉錄沒有 token，帳本上就是空白。
        Assert.False(AiUsageFeatureText.IsTokenBased(AiUsageFeature.TranscriptionComparison));
        Assert.Equal("轉錄比較", AiUsageFeatureText.Describe(AiUsageFeature.TranscriptionComparison));
        Assert.Equal(10, (int)AiUsageFeature.TranscriptionComparison);
    }

    [Fact]
    public void ExcerptArguments_ShouldMatchTranscriptionMp3Format()
    {
        // 和正式轉錄同規格（16kHz 單聲道 32kbps），比較才公平；loglevel info 才拿得到結尾的 time=（實際長度）。
        var args = FfmpegMediaConverter.BuildExcerptArguments(@"C:\m\a.webm", @"C:\t\x.mp3", 120, 300);

        Assert.Contains("-ss 120 -t 300 -i \"C:\\m\\a.webm\"", args);
        Assert.Contains("-ac 1 -ar 16000 -c:a libmp3lame -b:a 32k", args);
        Assert.Contains("-loglevel info", args);
        Assert.EndsWith("\"C:\\t\\x.mp3\"", args);
    }
}
