using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 資料庫用量頁的「檔案儲存用量」（0.4.120）：各外部目錄的大小與檔案數、依磁碟去重的可用空間。
/// </summary>
public sealed class StorageUsageServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"meetingrecord-storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetReport_ShouldMeasureEachConfiguredDirectory()
    {
        var media = Path.Combine(root, "MeetingMedia");
        Directory.CreateDirectory(Path.Combine(media, "2026", "10"));
        File.WriteAllBytes(Path.Combine(media, "2026", "10", "a.mp3"), new byte[100]);
        File.WriteAllBytes(Path.Combine(media, "b.mp3"), new byte[50]);

        var settings = new SystemSettings();
        settings.ExternalFileSystem.MeetingMediaPath = media;
        settings.ExternalFileSystem.AiChatPath = Path.Combine(root, "NotCreated");

        var report = CreateService(settings, logDirectory: string.Empty).GetReport();

        var mediaRow = report.Directories.Single(x => x.Label == "會議影音");
        Assert.True(mediaRow.Exists);
        Assert.Equal(150L, mediaRow.Bytes);
        Assert.Equal(2, mediaRow.FileCount);

        var missing = report.Directories.Single(x => x.Label == "AI 問答");
        Assert.False(missing.Exists);
        Assert.Equal(0L, missing.Bytes);

        // 未設定的目錄照樣列出來（顯示「未設定」），不能默默消失。
        Assert.Contains(report.Directories, x => x.Label == "日誌" && x.Path == string.Empty);
        Assert.Equal(150L, report.TotalBytes);
    }

    [Fact]
    public void GetReport_ShouldGroupDirectoriesOnTheSameDrive()
    {
        Directory.CreateDirectory(root);
        var settings = new SystemSettings();
        settings.ExternalFileSystem.MeetingMediaPath = Path.Combine(root, "a");
        settings.ExternalFileSystem.MeetingTranscriptPath = Path.Combine(root, "b");

        var report = CreateService(settings, logDirectory: Path.Combine(root, "logs")).GetReport();

        var drive = Assert.Single(report.Drives);
        Assert.Contains("會議影音", drive.Labels);
        Assert.Contains("逐字稿", drive.Labels);
        Assert.Contains("日誌", drive.Labels);
        Assert.True(drive.TotalBytes > 0);
    }

    private static StorageUsageService CreateService(SystemSettings settings, string logDirectory)
        => new(Options.Create(settings), new FakeLogPathResolver(logDirectory), NullLogger<StorageUsageService>.Instance);

    private sealed class FakeLogPathResolver(string directory) : INLogFilePathResolver
    {
        public string GetLogDirectory() => directory;

        public string GetLogFilePath(DateOnly date) => string.Empty;

        public IReadOnlyList<string> GetExistingFilesInRange(DateOnly from, DateOnly to) => [];
    }
}
