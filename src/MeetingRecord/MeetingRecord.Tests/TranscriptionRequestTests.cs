using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 轉錄管線中兩段「組字串」邏輯的單元測試：Azure OpenAI 端點位址與 FFmpeg 命令列參數。
/// 這裡刻意只測純函式——不打真實 API、不啟動真實 ffmpeg。
/// </summary>
public sealed class TranscriptionRequestTests
{
    #region Azure OpenAI 端點組裝

    [Fact]
    public void BuildRequestUri_ShouldComposeDeploymentAndApiVersion()
    {
        var uri = AzureOpenAiTranscriptionProvider.BuildRequestUri(
            "https://demo.openai.azure.com/",
            "gpt-4o-transcribe",
            "2025-03-01-preview");

        Assert.Equal(
            "https://demo.openai.azure.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions?api-version=2025-03-01-preview",
            uri.ToString());
    }

    [Fact]
    public void BuildRequestUri_ShouldNotProduceDoubleSlash_WhenEndpointHasNoTrailingSlash()
    {
        var uri = AzureOpenAiTranscriptionProvider.BuildRequestUri(
            "https://demo.openai.azure.com",
            "whisper",
            "2024-06-01");

        Assert.DoesNotContain("azure.com//", uri.ToString());
    }

    [Theory]
    [InlineData("", "gpt-4o-transcribe", "2025-03-01-preview")]
    [InlineData("https://demo.openai.azure.com/", "", "2025-03-01-preview")]
    [InlineData("https://demo.openai.azure.com/", "gpt-4o-transcribe", "")]
    public void BuildRequestUri_ShouldThrow_WhenRequiredPartIsBlank(string endpoint, string deployment, string apiVersion)
    {
        Assert.Throws<InvalidOperationException>(
            () => AzureOpenAiTranscriptionProvider.BuildRequestUri(endpoint, deployment, apiVersion));
    }

    #endregion

    #region 轉錄 API 版本回退

    [Fact]
    public void EffectiveTranscriptionApiVersion_ShouldPreferTranscriptionApiVersion()
    {
        var provider = new LlmProviderSettings
        {
            ApiVersion = "2024-10-21",
            TranscriptionApiVersion = "2025-03-01-preview",
        };

        Assert.Equal("2025-03-01-preview", provider.EffectiveTranscriptionApiVersion);
    }

    [Fact]
    public void EffectiveTranscriptionApiVersion_ShouldFallBackToApiVersion()
    {
        var provider = new LlmProviderSettings { ApiVersion = "2024-10-21" };

        Assert.Equal("2024-10-21", provider.EffectiveTranscriptionApiVersion);
    }

    #endregion

    #region FFmpeg 參數組裝

    [Fact]
    public void BuildSegmentArguments_ShouldDropVideoAndDownmixToMonoMp3()
    {
        var arguments = FfmpegMediaConverter.BuildSegmentArguments(
            @"C:\media\meeting.mp4",
            @"C:\temp\part_%04d.mp3",
            900);

        // -vn 讓影片只取聲音；單聲道 16kHz 32kbps 是讓每個分段遠低於轉錄 API 單檔上限的關鍵。
        Assert.Contains("-vn", arguments);
        Assert.Contains("-ac 1", arguments);
        Assert.Contains("-ar 16000", arguments);
        Assert.Contains("-c:a libmp3lame", arguments);
        Assert.Contains("-b:a 32k", arguments);
    }

    [Fact]
    public void BuildSegmentArguments_ShouldAlwaysUseSegmentMuxer()
    {
        var arguments = FfmpegMediaConverter.BuildSegmentArguments(
            @"C:\media\meeting.mp3",
            @"C:\temp\part_%04d.mp3",
            900);

        Assert.Contains("-f segment", arguments);
        Assert.Contains("-segment_time 900", arguments);
        Assert.Contains("-segment_format mp3", arguments);
    }

    [Fact]
    public void BuildSegmentArguments_ShouldQuotePathsSoSpacesSurvive()
    {
        var arguments = FfmpegMediaConverter.BuildSegmentArguments(
            @"C:\my media\會議 錄音.mp4",
            @"C:\temp dir\part_%04d.mp3",
            900);

        Assert.Contains(@"""C:\my media\會議 錄音.mp4""", arguments);
        Assert.Contains(@"""C:\temp dir\part_%04d.mp3""", arguments);
    }

    #endregion

    #region 影音允收政策

    [Theory]
    [InlineData("錄音.mp3")]
    [InlineData("錄音.WMA")]
    [InlineData("會議.mp4")]
    [InlineData("會議.mkv")]
    public void IsAllowedFileName_ShouldAcceptCommonMediaFormats(string fileName)
    {
        Assert.True(MeetingMediaPolicy.IsAllowedFileName(fileName));
    }

    [Theory]
    [InlineData("紀錄.txt")]
    [InlineData("封存.zip")]
    [InlineData("沒有副檔名")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowedFileName_ShouldRejectNonMediaFiles(string? fileName)
    {
        Assert.False(MeetingMediaPolicy.IsAllowedFileName(fileName));
    }

    [Fact]
    public void MaxUploadFileSize_ShouldBeOneGigabyte()
    {
        Assert.Equal(1024L * 1024L * 1024L, MeetingMediaPolicy.MaxUploadFileSize);
    }

    #endregion
}
