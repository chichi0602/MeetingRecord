using System.Diagnostics;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>一個外部目錄的用量。</summary>
/// <param name="Label">顯示名稱，例如「會議影音」。</param>
/// <param name="Purpose">放什麼東西，給管理者判斷要不要清。</param>
/// <param name="Path">設定的路徑；未設定時為空字串。</param>
/// <param name="Exists">目錄是否存在。</param>
/// <param name="Bytes">目錄（含子目錄）的總位元組數。</param>
/// <param name="FileCount">檔案數。</param>
public sealed record DirectoryUsage(string Label, string Purpose, string Path, bool Exists, long Bytes, int FileCount);

/// <summary>一顆磁碟的可用空間，以及哪些目錄放在上面。</summary>
public sealed record DriveUsage(string Root, long FreeBytes, long TotalBytes, IReadOnlyList<string> Labels);

public sealed class StorageUsageReport
{
    public IReadOnlyList<DirectoryUsage> Directories { get; init; } = [];

    public IReadOnlyList<DriveUsage> Drives { get; init; } = [];

    public long TotalBytes => Directories.Sum(x => x.Bytes);

    /// <summary>非致命問題：某顆磁碟讀不到等。</summary>
    public List<string> Warnings { get; } = [];

    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// 「檔案儲存用量」（0.4.120，資料庫用量頁下半部）。
///
/// <para>本系統真正吃空間的是資料庫**外**的檔案：會議影音、逐字稿、專案附件、AI 問答與日誌。
/// 這裡把 <see cref="ExternalFileSystem"/> 各目錄與日誌目錄逐一量出大小與檔案數，並依磁碟去重列出可用空間。</para>
///
/// <para>⚠️ 量的是磁碟現況（同 <see cref="DirectorySizeCalculator"/>）：資料庫已刪、檔案還在的孤兒檔也會算進去。
/// 影音目錄可能有數 GB、上萬個檔案，呼叫端要在背景執行緒跑，不要卡住 Blazor circuit。</para>
/// </summary>
public sealed class StorageUsageService
{
    private readonly IOptions<SystemSettings> systemSettings;
    private readonly INLogFilePathResolver logPathResolver;
    private readonly ILogger<StorageUsageService> logger;

    public StorageUsageService(
        IOptions<SystemSettings> systemSettings,
        INLogFilePathResolver logPathResolver,
        ILogger<StorageUsageService> logger)
    {
        this.systemSettings = systemSettings;
        this.logPathResolver = logPathResolver;
        this.logger = logger;
    }

    /// <summary>要量的目錄清單。新增外部目錄時加在這裡，畫面自動多一列。</summary>
    public IReadOnlyList<(string Label, string Purpose, string Path)> GetTargets()
    {
        var fs = systemSettings.Value.ExternalFileSystem;
        return
        [
            ("資料庫", "SQLite 資料庫檔（BackendDB.db）", fs.DatabasePath),
            ("會議影音", "上傳的原始錄音／錄影檔", fs.MeetingMediaPath),
            ("逐字稿", "語音轉錄產生的 .txt", fs.MeetingTranscriptPath),
            ("專案附件", "專案上傳的附件", fs.ProjectFilePath),
            ("AI 問答", "AI 問答對話與提問附件", fs.AiChatPath),
            ("上傳暫存", "通用上傳暫存目錄", fs.UploadPath),
            ("下載", "/UploadFiles 對外服務的目錄", fs.DownloadPath),
            ("登入金鑰", "Data Protection 金鑰（刪除會讓所有人被登出）", fs.DataProtectionKeyPath),
            ("日誌", "NLog 日誌檔（保留 30 天）", logPathResolver.GetLogDirectory()),
        ];
    }

    public StorageUsageReport GetReport()
    {
        var stopwatch = Stopwatch.StartNew();
        var directories = new List<DirectoryUsage>();

        foreach (var (label, purpose, path) in GetTargets())
        {
            var exists = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            var (bytes, fileCount) = DirectorySizeCalculator.MeasureDetailed(path);
            directories.Add(new DirectoryUsage(label, purpose, path ?? string.Empty, exists, bytes, fileCount));
        }

        var warnings = new List<string>();
        var report = new StorageUsageReport
        {
            Directories = directories,
            Drives = CollectDrives(directories, warnings),
        };
        report.Warnings.AddRange(warnings);

        stopwatch.Stop();
        report.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        logger.LogInformation(
            "Storage usage collected. Elapsed={ElapsedMs}ms, Directories={Directories}, Total={Total}",
            report.ElapsedMilliseconds, directories.Count, FileSizeFormatter.Describe(report.TotalBytes));

        return report;
    }

    /// <summary>依磁碟根目錄去重；同一顆磁碟上的目錄合併成一列。</summary>
    private List<DriveUsage> CollectDrives(IReadOnlyList<DirectoryUsage> directories, List<string> warnings)
    {
        var result = new List<DriveUsage>();

        var groups = directories
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => (Root: SafeGetRoot(x.Path), x.Label))
            .Where(x => !string.IsNullOrEmpty(x.Root))
            .GroupBy(x => x.Root!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            try
            {
                var drive = new DriveInfo(group.Key);
                result.Add(new DriveUsage(
                    drive.Name,
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    group.Select(x => x.Label).ToList()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "Failed to read drive information. Root={Root}", group.Key);
                warnings.Add($"無法讀取磁碟 {group.Key} 的空間資訊（{ex.GetType().Name}）。");
            }
        }

        return result;
    }

    private static string? SafeGetRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
