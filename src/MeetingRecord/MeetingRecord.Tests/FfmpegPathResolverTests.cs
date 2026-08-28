using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Tests;

public sealed class FfmpegPathResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exists_WithBlankPath_ShouldBeFalse(string? path)
    {
        Assert.False(FfmpegPathResolver.Exists(path));
    }

    [Fact]
    public void Exists_WithRootedPathToExistingFile_ShouldBeTrue()
    {
        // 拿執行中的測試主機當「一定存在的檔案」，不必在磁碟上另外造假資料。
        Assert.True(FfmpegPathResolver.Exists(Environment.ProcessPath));
    }

    [Fact]
    public void Exists_WithRootedPathToMissingFile_ShouldBeFalse()
    {
        // 這正是這次的故障情境：路徑有填，但檔案根本不在。
        Assert.False(FfmpegPathResolver.Exists(@"C:\ffmpeg\bin\ffmpeg.exe"));
    }

    [Fact]
    public void Exists_WithBareFileNameOnSearchPath_ShouldBeTrue()
    {
        // 只填檔名要能沿 PATH 找到，否則把 FfmpegPath 設成 ffmpeg 會被誤判成不存在。
        Assert.True(FfmpegPathResolver.Exists("cmd"));
    }

    [Fact]
    public void Exists_WithBareFileNameNotOnSearchPath_ShouldBeFalse()
    {
        Assert.False(FfmpegPathResolver.Exists("definitely-not-an-executable-on-path"));
    }

    [Fact]
    public void Exists_WithRelativePath_ShouldNotFallBackToSearchPath()
    {
        // 帶了目錄資訊就當路徑解析；不該因為 PATH 上剛好有同名檔案而誤判為存在。
        Assert.False(FfmpegPathResolver.Exists(@"nowhere\cmd.exe"));
    }
}
