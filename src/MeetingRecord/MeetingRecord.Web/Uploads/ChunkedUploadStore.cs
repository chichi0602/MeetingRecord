using System.Collections.Concurrent;
using System.IO.Pipelines;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Uploads;

/// <summary>
/// 分塊上傳的工作階段登記處（1.0.59，singleton，單機記憶體，與既有佇列一致）。
///
/// <para>
/// 為什麼要有它：大檔不能整個經一條長連線（SignalR）串流上傳——IIS 上傳一個 127 MB 的檔案
/// 約 11 秒就斷線，而且連帶拖垮整條連線（見 docs/changelog/2026-10-08-會議上傳進度與連線中斷.md）。
/// 所以瀏覽器以 HTTP 一塊一塊送（<c>UploadChunkController</c>）。
/// </para>
///
/// <para>
/// 設計重點：每個工作階段對外提供一個 <see cref="ChunkedUploadSession.Stream"/>，
/// 由 <c>MeetingMediaUploadJobs</c>／<c>ProjectAttachmentUploads</c> 當成一般的檔案串流交給服務層——
/// 驗證、進度、取消、取消時刪會議、失敗回報都在服務層，這裡只負責把分塊接成串流。
/// </para>
/// </summary>
public sealed class ChunkedUploadStore
{
    /// <summary>每塊 8 MB：遠低於 IIS／Kestrel 預設 30 MB 的請求上限，1 GB 約 128 塊。</summary>
    public const int ChunkSize = 8 * 1024 * 1024;

    /// <summary>
    /// 超過這段時間沒收到下一塊就當成中斷（例如使用者關掉分頁）。
    /// 瀏覽器端每塊失敗會重試 3 次（1／3／9 秒），正常網路下一塊遠不到這個時間。
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    public const string IdleTimeoutMessage = "上傳中斷：太久沒有收到檔案資料（可能關掉了頁面或網路斷線），請重新上傳。";

    private readonly ConcurrentDictionary<Guid, ChunkedUploadSession> sessions = new();
    private readonly ILogger<ChunkedUploadStore> logger;
    private readonly TimeSpan idleTimeout;

    public ChunkedUploadStore(ILogger<ChunkedUploadStore> logger)
        : this(logger, DefaultIdleTimeout)
    {
    }

    internal ChunkedUploadStore(ILogger<ChunkedUploadStore> logger, TimeSpan idleTimeout)
    {
        this.logger = logger;
        this.idleTimeout = idleTimeout;
    }

    /// <summary>建立一個工作階段；只有 <paramref name="ownerUserId"/> 本人送得進分塊。</summary>
    public ChunkedUploadSession Create(int ownerUserId, long fileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);

        var session = new ChunkedUploadSession(Guid.NewGuid(), ownerUserId, fileSize, idleTimeout, Remove);
        sessions[session.Id] = session;
        logger.LogInformation(
            "Chunked upload session created. UploadId={UploadId}, UserId={UserId}, FileSize={FileSize}, ChunkCount={ChunkCount}",
            session.Id,
            ownerUserId,
            fileSize,
            session.ChunkCount);
        return session;
    }

    /// <summary>
    /// 找工作階段。不存在、已結束或不是本人的一律回 false——呼叫端一律回 404，
    /// 不要讓別人分得出「這個編號存在但不是你的」。
    /// </summary>
    public bool TryGet(Guid uploadId, int userId, out ChunkedUploadSession session)
    {
        if (sessions.TryGetValue(uploadId, out var found) && found.OwnerUserId == userId)
        {
            session = found;
            return true;
        }

        session = null!;
        return false;
    }

    internal int Count => sessions.Count;

    private void Remove(Guid uploadId)
    {
        if (sessions.TryRemove(uploadId, out _))
        {
            logger.LogDebug("Chunked upload session removed. UploadId={UploadId}", uploadId);
        }
    }
}

public enum ChunkWriteStatus
{
    /// <summary>收下了，還有下一塊。</summary>
    Accepted,

    /// <summary>收下了，這是最後一塊。</summary>
    Completed,

    /// <summary>這一塊之前已經收過（瀏覽器重送），不重複寫入。</summary>
    AlreadyReceived,

    /// <summary>塊的編號或長度不對。</summary>
    Rejected,

    /// <summary>讀取端已經不要了（存檔完成、被拒絕、被取消、逾時），瀏覽器應該停止送。</summary>
    Aborted,
}

public sealed record ChunkWriteResult(ChunkWriteStatus Status, int ReceivedChunks, string? Message = null);

/// <summary>
/// 一趟分塊上傳。寫入端是 HTTP 分塊，讀取端是 <see cref="Stream"/>（交給服務層照常讀）。
/// 中間用 <see cref="Pipe"/> 接起來：讀得慢時寫入端會等（背壓），不會把整個檔案堆在記憶體裡。
/// </summary>
public sealed class ChunkedUploadSession : IAsyncDisposable
{
    private readonly Pipe pipe;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Timer idleTimer;
    private readonly TimeSpan idleTimeout;
    private readonly Action<Guid> onEnded;
    private int nextIndex;
    private int ended;
    private bool writerCompleted;

    internal ChunkedUploadSession(Guid id, int ownerUserId, long fileSize, TimeSpan idleTimeout, Action<Guid> onEnded)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        FileSize = fileSize;
        ChunkCount = (int)((fileSize + ChunkedUploadStore.ChunkSize - 1) / ChunkedUploadStore.ChunkSize);
        this.idleTimeout = idleTimeout;
        this.onEnded = onEnded;

        // 背壓：服務層還沒讀完上一塊時，下一塊的請求會在這裡等，每趟上傳在記憶體裡最多約一兩塊（8～16 MB）。
        pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: ChunkedUploadStore.ChunkSize * 2L,
            resumeWriterThreshold: ChunkedUploadStore.ChunkSize,
            useSynchronizationContext: false));
        Stream = pipe.Reader.AsStream();

        idleTimer = new Timer(_ => Fail(ChunkedUploadStore.IdleTimeoutMessage), null, idleTimeout, Timeout.InfiniteTimeSpan);

        if (ChunkCount == 0)
        {
            // 0 位元組的檔案沒有任何分塊，讀取端直接讀到結尾。
            pipe.Writer.Complete();
            writerCompleted = true;
            idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public Guid Id { get; }

    public int OwnerUserId { get; }

    public long FileSize { get; }

    public int ChunkCount { get; }

    /// <summary>交給服務層讀的串流；讀到結尾＝檔案收齊。釋放它（或整個工作階段）就會讓瀏覽器停止送。</summary>
    public Stream Stream { get; }

    /// <summary>
    /// 寫入第 <paramref name="index"/> 塊（從 0 起）。依序寫入；重送已收過的塊不會重複寫。
    /// 除了最後一塊，每塊都必須剛好 <see cref="ChunkedUploadStore.ChunkSize"/>。
    /// </summary>
    public async Task<ChunkWriteResult> WriteChunkAsync(int index, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref ended) == 1)
            {
                return new ChunkWriteResult(ChunkWriteStatus.Aborted, nextIndex, "上傳已經停止。");
            }

            if (index < 0 || index >= ChunkCount)
            {
                return new ChunkWriteResult(ChunkWriteStatus.Rejected, nextIndex, $"分塊編號 {index} 超出範圍（共 {ChunkCount} 塊）。");
            }

            if (index < nextIndex)
            {
                return new ChunkWriteResult(ChunkWriteStatus.AlreadyReceived, nextIndex);
            }

            if (index > nextIndex)
            {
                return new ChunkWriteResult(ChunkWriteStatus.Rejected, nextIndex, $"分塊要依序送出，下一塊應該是 {nextIndex}。");
            }

            var expectedLength = index == ChunkCount - 1
                ? (int)(FileSize - (long)index * ChunkedUploadStore.ChunkSize)
                : ChunkedUploadStore.ChunkSize;
            if (data.Length != expectedLength)
            {
                return new ChunkWriteResult(ChunkWriteStatus.Rejected, nextIndex, $"第 {index} 塊應該是 {expectedLength} 位元組，收到 {data.Length}。");
            }

            idleTimer.Change(idleTimeout, Timeout.InfiniteTimeSpan);

            // ⚠️ 寫入不吃請求的取消權杖：WriteAsync 被取消時資料可能已經進了管線，nextIndex 卻還沒加，
            //    瀏覽器重送同一塊就會寫兩次。要中止一律走 Fail（CancelPendingFlush）或讀取端結束。
            var flush = await pipe.Writer.WriteAsync(data, CancellationToken.None);
            if (flush.IsCompleted || flush.IsCanceled)
            {
                // IsCompleted：讀取端已經結束（服務層拒絕、取消或存完了）；IsCanceled：Fail 正在中止這趟上傳。
                End();
                return new ChunkWriteResult(ChunkWriteStatus.Aborted, nextIndex, "上傳已經停止。");
            }

            nextIndex++;
            if (nextIndex == ChunkCount)
            {
                idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                await pipe.Writer.CompleteAsync();
                writerCompleted = true;
                return new ChunkWriteResult(ChunkWriteStatus.Completed, nextIndex);
            }

            return new ChunkWriteResult(ChunkWriteStatus.Accepted, nextIndex);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// 瀏覽器端放棄（重試用完、登入失效）或逾時：讓讀取端拿到 <see cref="UploadInterruptedException"/>（是一種 <see cref="IOException"/>），
    /// 服務層就會走既有的「上傳失敗」流程（刪孤兒檔、通知器標失敗），並把 <paramref name="message"/> 這個原因帶到畫面（1.0.67）。
    /// </summary>
    public void Fail(string message)
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
        {
            return;
        }

        idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // 寫入端不是執行緒安全的：先叫醒可能卡在背壓等待的那次寫入，等它放掉鎖，再完成寫入端。
        pipe.Writer.CancelPendingFlush();
        writeLock.Wait();
        try
        {
            // 已經收齊的話讀取端照常讀完，不必中止。
            if (!writerCompleted)
            {
                pipe.Writer.Complete(new UploadInterruptedException(message));
                writerCompleted = true;
            }
        }
        finally
        {
            writeLock.Release();
        }

        onEnded(Id);
    }

    /// <summary>讀取端用完了（不論成敗）：結束工作階段，之後送來的分塊一律回「已停止」。</summary>
    public async ValueTask DisposeAsync()
    {
        End();
        await pipe.Reader.CompleteAsync();
        await idleTimer.DisposeAsync();
    }

    private void End()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
        {
            return;
        }

        idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        onEnded(Id);
    }
}
