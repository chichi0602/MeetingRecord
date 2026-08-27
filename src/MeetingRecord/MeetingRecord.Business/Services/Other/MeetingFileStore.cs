using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 會議影音檔與逐字稿的實體檔案存取。
///
/// 目錄組織沿用專案附件的「年／月 + GUID 檔名」策略（見 docs/features/檔案上傳機制.md）；
/// 根目錄一律取自 <see cref="SystemSettings.ExternalFileSystem"/>，禁止直接讀 IConfiguration。
/// 資料庫只保存相對路徑，實體檔案的建立與刪除都集中在這個類別。
/// </summary>
public class MeetingFileStore
{
    /// <summary>寫入檔案時的緩衝區大小，同時決定上傳進度的回報頻率。</summary>
    private const int CopyBufferSize = 81920;

    private readonly string mediaRootPath;
    private readonly string transcriptRootPath;
    private readonly ILogger<MeetingFileStore> logger;

    public MeetingFileStore(IOptions<SystemSettings> systemSettings, ILogger<MeetingFileStore> logger)
    {
        mediaRootPath = systemSettings.Value.ExternalFileSystem.MeetingMediaPath;
        transcriptRootPath = systemSettings.Value.ExternalFileSystem.MeetingTranscriptPath;
        this.logger = logger;
    }

    public string GetMediaFullPath(string relativePath) => Combine(mediaRootPath, relativePath);

    public string GetTranscriptFullPath(string relativePath) => Combine(transcriptRootPath, relativePath);

    /// <summary>
    /// 將上傳串流落地為實體檔案，並在複製過程中回報百分比進度。
    /// </summary>
    public async Task<StoredMediaFile> SaveMediaAsync(
        DateTime createdAt,
        MeetingMediaUploadInput uploadFile,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var relativePath = BuildRelativePath(createdAt, $"{Guid.NewGuid():N}{extension}");
        var fullPath = GetMediaFullPath(relativePath);

        EnsureParentDirectory(fullPath);

        if (uploadFile.Content.CanSeek)
        {
            uploadFile.Content.Position = 0;
        }

        long written = 0;
        var lastReportedPercent = -1;
        progress?.Report(0);

        try
        {
            await using (var targetStream = File.Create(fullPath))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await uploadFile.Content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    if (uploadFile.FileSize <= 0)
                    {
                        continue;
                    }

                    var percent = (int)Math.Min(100, written * 100 / uploadFile.FileSize);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }
        }
        catch
        {
            TryDeleteFile(fullPath);
            throw;
        }

        progress?.Report(100);

        var contentType = string.IsNullOrWhiteSpace(uploadFile.ContentType)
            ? "application/octet-stream"
            : uploadFile.ContentType;

        logger.LogInformation(
            "Saved meeting media file. RelativePath={RelativePath}, FileSize={FileSize}",
            relativePath,
            written);

        return new StoredMediaFile(
            originalFileName,
            Path.GetFileName(fullPath),
            relativePath,
            contentType,
            written);
    }

    /// <summary>
    /// 寫入逐字稿並回傳相對路徑。使用 UTF-8 含 BOM，避免使用者以記事本開啟時出現亂碼。
    /// </summary>
    public async Task<string> SaveTranscriptAsync(DateTime createdAt, string content, CancellationToken cancellationToken)
    {
        var relativePath = BuildRelativePath(createdAt, $"{Guid.NewGuid():N}.txt");
        var fullPath = GetTranscriptFullPath(relativePath);

        EnsureParentDirectory(fullPath);
        await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

        logger.LogInformation("Saved meeting transcript file. RelativePath={RelativePath}", relativePath);
        return relativePath;
    }

    /// <summary>讀取逐字稿內容；檔案不存在時回傳 null。</summary>
    public async Task<string?> ReadTranscriptAsync(string? relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var fullPath = GetTranscriptFullPath(relativePath);
        if (!File.Exists(fullPath))
        {
            logger.LogWarning("Meeting transcript file is missing. RelativePath={RelativePath}", relativePath);
            return null;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    /// <summary>刪除影音檔實體檔案。失敗只記 Warning，不阻斷主流程。</summary>
    public void TryDeleteMedia(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        TryDeleteFile(GetMediaFullPath(relativePath));
    }

    /// <summary>刪除逐字稿實體檔案。失敗只記 Warning，不阻斷主流程。</summary>
    public void TryDeleteTranscript(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        TryDeleteFile(GetTranscriptFullPath(relativePath));
    }

    private static string BuildRelativePath(DateTime createdAt, string fileName)
    {
        var year = createdAt.Year.ToString("0000");
        var month = createdAt.Month.ToString("00");
        return Path.Combine(year, month, fileName).Replace('\\', '/');
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    private static string Combine(string rootPath, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(rootPath, normalized);
    }

    private void TryDeleteFile(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            // 實體檔案刪不掉不應阻斷資料列的刪除（檔案可能已被外部移動或鎖定）。
            logger.LogWarning(ex, "Failed to delete meeting file. FullPath={FullPath}", fullPath);
        }
    }
}

/// <summary>影音檔落地後的中繼資料。</summary>
public sealed record StoredMediaFile(
    string OriginalFileName,
    string StoredFileName,
    string RelativePath,
    string ContentType,
    long FileSize);
