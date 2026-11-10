using System.Collections.Concurrent;
using MeetingRecord.Models.Systems;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Uploads;

/// <summary>
/// WASM 頁的專案附件上傳（1.1.24，WASM 遷移 M3，singleton）。比照 Server 頁：選好檔案就開始傳、先落到暫存檔
/// （<see cref="PendingUploadTempFiles.DefaultDirectory"/>），按存檔時才把暫存檔交給 <c>ProjectService</c>。
///
/// <para>
/// 瀏覽器照舊以 <c>chunk-upload.js</c> 分塊送，這裡在背景把分塊接成的串流寫進暫存檔；前端以 <c>GET api/v1/uploads/{id}</c> 查到
/// <see cref="UploadStates.Succeeded"/> 才算選好。存檔成功才刪暫存檔（<see cref="Release"/>），失敗時留著讓使用者直接重送；
/// 沒用到的由網站啟動時的 <see cref="PendingUploadTempFiles.DeleteStale"/>（24 小時）與這裡的過期清理收掉。
/// </para>
/// </summary>
public sealed class ProjectAttachmentUploads(ChunkedUploadStore store, ILogger<ProjectAttachmentUploads> logger)
{
    /// <summary>傳完但一直沒存檔的暫存檔保留多久。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, ProjectAttachmentUpload> uploads = new();

    /// <summary>建立工作階段並在背景開始寫暫存檔。呼叫前要先檢查過檔案大小。</summary>
    public ProjectAttachmentUpload Start(int userId, UploadSessionRequestDto request)
    {
        PurgeExpired();

        var session = store.Create(userId, request.Size);
        var upload = new ProjectAttachmentUpload(
            session.Id,
            userId,
            Path.GetFileName(request.FileName),
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            request.Size,
            Path.Combine(PendingUploadTempFiles.DefaultDirectory, $"{session.Id:N}.tmp"),
            session.ChunkCount);
        uploads[upload.UploadId] = upload;

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => RunAsync(upload, session));
        }

        return upload;
    }

    /// <summary>找自己的上傳；不存在或不是自己的一律 false（呼叫端回 404）。</summary>
    public bool TryGet(Guid uploadId, int userId, out ProjectAttachmentUpload upload)
    {
        if (uploads.TryGetValue(uploadId, out var found) && found.OwnerUserId == userId)
        {
            upload = found;
            return true;
        }

        upload = null!;
        return false;
    }

    /// <summary>專案存檔成功：刪掉暫存檔、不再能用。</summary>
    public void Release(ProjectAttachmentUpload upload)
    {
        uploads.TryRemove(upload.UploadId, out _);
        TryDelete(upload.TempPath);
    }

    private async Task RunAsync(ProjectAttachmentUpload upload, ChunkedUploadSession session)
    {
        try
        {
            await using (session)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(upload.TempPath)!);
                await using var target = File.Create(upload.TempPath);
                await session.Stream.CopyToAsync(target, upload.Cancellation.Token);
            }

            upload.Finish(UploadStates.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(upload.TempPath);
            upload.Finish(UploadStates.Cancelled, "已取消上傳。");
        }
        catch (UploadInterruptedException ex)
        {
            // 瀏覽器端放棄（登入失效、重試用完）或逾時：講明原因（同 Server 頁 1.0.67），使用者才知道要重新登入還是重選。
            TryDelete(upload.TempPath);
            upload.Finish(UploadStates.Failed, $"{upload.FileName} 上傳失敗：{ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background project attachment upload failed. UploadId={UploadId}, FileName={FileName}", upload.UploadId, upload.FileName);
            TryDelete(upload.TempPath);
            upload.Finish(UploadStates.Failed, $"{upload.FileName} 讀取失敗，請重新選擇。");
        }
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.Now - Retention;
        foreach (var (id, upload) in uploads)
        {
            if (upload.CreatedAt < cutoff && uploads.TryRemove(id, out _))
            {
                TryDelete(upload.TempPath);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete pending project attachment. Path={Path}", path);
        }
    }
}

/// <summary>一個 WASM 頁上傳中（或已傳完、等存檔）的專案附件。</summary>
public sealed class ProjectAttachmentUpload(Guid uploadId, int ownerUserId, string fileName, string contentType, long fileSize, string tempPath, int chunkCount)
{
    private volatile string state = UploadStates.Uploading;

    public Guid UploadId { get; } = uploadId;

    public int OwnerUserId { get; } = ownerUserId;

    public string FileName { get; } = fileName;

    public string ContentType { get; } = contentType;

    public long FileSize { get; } = fileSize;

    public string TempPath { get; } = tempPath;

    public int ChunkCount { get; } = chunkCount;

    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary><c>DELETE api/v1/uploads/{id}</c> 觸發。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public string State => state;

    public string? Message { get; private set; }

    internal void Finish(string finalState, string? message)
    {
        Message = message;
        state = finalState;
    }

    /// <summary>存檔時交給服務層的輸入；串流由呼叫端釋放。</summary>
    public ProjectUploadFileInput OpenInput() => new()
    {
        FileName = FileName,
        ContentType = ContentType,
        FileSize = FileSize,
        Content = File.OpenRead(TempPath),
    };

    public UploadStatusDto ToDto() => new()
    {
        UploadId = UploadId,
        State = State,
        Message = Message,
    };
}
