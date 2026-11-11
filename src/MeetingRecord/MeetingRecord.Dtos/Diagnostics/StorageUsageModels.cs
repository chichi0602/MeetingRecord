using System.Text.Json.Serialization;

// 1.1.32（WASM 遷移 M5）：從 Web/Diagnostics/StorageUsageService.cs 搬來，檔案儲存用量 API 與 WASM 頁共用同一份型別。
namespace MeetingRecord.Dtos.Diagnostics;

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

    [JsonIgnore]
    public long TotalBytes => Directories.Sum(x => x.Bytes);

    /// <summary>非致命問題：某顆磁碟讀不到等。（1.1.32 改成 init：要能從 JSON 還原。）</summary>
    public List<string> Warnings { get; init; } = [];

    public long ElapsedMilliseconds { get; set; }
}
