using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Tests;

/// <summary>
/// 專案附件暫存檔的啟動清理（1.0.65）：網站當掉或重啟時編輯中的暫存檔、以及 1.0.64 以前存檔沒刪掉的殘檔，
/// 啟動時清掉 24 小時以前的；24 小時內的留著（IIS 回收時新舊行程短暫並存，舊行程可能還在用）。
/// </summary>
public sealed class PendingUploadTempFilesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PendingUploadTempFilesTests", Guid.NewGuid().ToString("N"));

    public PendingUploadTempFilesTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void DeleteStale_ShouldDeleteOldTempFiles_AndKeepRecentOnes()
    {
        var old = CreateFile("old.tmp", Now.AddHours(-25));
        var recent = CreateFile("recent.tmp", Now.AddHours(-23));

        var deleted = PendingUploadTempFiles.DeleteStale(directory, TimeSpan.FromHours(24), Now, NullLogger.Instance);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void DeleteStale_ShouldNotTouchNonTempFiles()
    {
        var other = CreateFile("note.txt", Now.AddDays(-10));

        var deleted = PendingUploadTempFiles.DeleteStale(directory, TimeSpan.FromHours(24), Now, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void DeleteStale_MissingDirectory_ShouldReturnZero()
        => Assert.Equal(0, PendingUploadTempFiles.DeleteStale(Path.Combine(directory, "missing"), TimeSpan.FromHours(24), Now, NullLogger.Instance));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string CreateFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
