using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 補登檔：程序即將結束、例外來不及寫進資料庫時先存檔，下次啟動再補進例外紀錄。
/// 呼叫它的時候記錄機制可能已經失效，所以任何情況都不得拋出。
/// </summary>
public sealed class CrashMarkerStoreTests : IDisposable
{
    private readonly string exceptionPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_ThenReadAll_ShouldRoundTripTheEntry()
    {
        CrashMarkerStore.Write(
            exceptionPath,
            new InvalidOperationException("database is locked"),
            ExceptionSources.Startup,
            "Application is stopping because of an unhandled exception.",
            "MeetingRecord.Web.Program");

        var (filePath, entry) = Assert.Single(CrashMarkerStore.ReadAll(exceptionPath));

        Assert.StartsWith(Path.Combine(exceptionPath, CrashMarkerStore.FolderName), filePath);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Equal("database is locked", entry.Message);
        Assert.Equal(ExceptionSources.Startup, entry.Source);
        Assert.Equal("Application is stopping because of an unhandled exception.", entry.Operation);
        Assert.Contains("database is locked", entry.StackTrace);
    }

    [Fact]
    public void Delete_ShouldRemoveTheMarker()
    {
        CrashMarkerStore.Write(exceptionPath, new InvalidOperationException("boom"), ExceptionSources.Process, "Op.", "Logger");
        var (filePath, _) = Assert.Single(CrashMarkerStore.ReadAll(exceptionPath));

        CrashMarkerStore.Delete(filePath);

        Assert.Empty(CrashMarkerStore.ReadAll(exceptionPath));
    }

    [Fact]
    public void ReadAll_ShouldSkipCorruptFilesAndKeepTheRest()
    {
        CrashMarkerStore.Write(exceptionPath, new InvalidOperationException("good"), ExceptionSources.Process, "Op.", "Logger");
        File.WriteAllText(Path.Combine(exceptionPath, CrashMarkerStore.FolderName, "00000000000000000-broken.json"), "{ not json");

        var entry = Assert.Single(CrashMarkerStore.ReadAll(exceptionPath)).Entry;

        Assert.Equal("good", entry.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingExceptionPath_ShouldNotThrow(string? path)
    {
        var exception = Record.Exception(() =>
        {
            CrashMarkerStore.Write(path, new InvalidOperationException("boom"), ExceptionSources.Startup, "Op.", "Logger");
            Assert.Empty(CrashMarkerStore.ReadAll(path));
        });

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(exceptionPath))
        {
            Directory.Delete(exceptionPath, recursive: true);
        }
    }
}
