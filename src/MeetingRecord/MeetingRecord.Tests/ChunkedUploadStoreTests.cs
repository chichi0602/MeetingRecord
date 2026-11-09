using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Tests;

/// <summary>
/// 分塊上傳工作階段（1.0.59）：瀏覽器以 HTTP 一塊一塊送，伺服器端把它接成一個 Stream 交給既有服務層讀。
/// </summary>
public sealed class ChunkedUploadStoreTests
{
    private const int Chunk = ChunkedUploadStore.ChunkSize;

    [Fact]
    public async Task Chunks_ShouldBeReadBackAsOneStream_InOrder()
    {
        var store = NewStore();
        var payload = Payload(Chunk * 2 + 123);
        await using var session = store.Create(ownerUserId: 7, payload.Length);
        Assert.Equal(3, session.ChunkCount);

        var reading = ReadAllAsync(session.Stream);
        Assert.Equal(ChunkWriteStatus.Accepted, (await session.WriteChunkAsync(0, payload.AsMemory(0, Chunk))).Status);
        Assert.Equal(ChunkWriteStatus.Accepted, (await session.WriteChunkAsync(1, payload.AsMemory(Chunk, Chunk))).Status);
        Assert.Equal(ChunkWriteStatus.Completed, (await session.WriteChunkAsync(2, payload.AsMemory(Chunk * 2))).Status);

        Assert.Equal(payload, await reading);
    }

    [Fact]
    public async Task ResendingAReceivedChunk_ShouldBeAcknowledged_WithoutWritingTwice()
    {
        // 瀏覽器沒收到回應就會重送；已寫進去的那塊不能再寫一次，否則檔案內容會重複。
        var store = NewStore();
        var payload = Payload(Chunk + 10);
        await using var session = store.Create(7, payload.Length);
        var reading = ReadAllAsync(session.Stream);

        await session.WriteChunkAsync(0, payload.AsMemory(0, Chunk));
        var resend = await session.WriteChunkAsync(0, payload.AsMemory(0, Chunk));
        await session.WriteChunkAsync(1, payload.AsMemory(Chunk));

        Assert.Equal(ChunkWriteStatus.AlreadyReceived, resend.Status);
        Assert.Equal(payload, await reading);
    }

    [Theory]
    [InlineData(1, "要依序")]
    [InlineData(5, "超出範圍")]
    [InlineData(-1, "超出範圍")]
    public async Task OutOfOrderOrOutOfRangeChunk_ShouldBeRejected(int index, string expectedMessage)
    {
        var store = NewStore();
        await using var session = store.Create(7, Chunk + 10);

        var result = await session.WriteChunkAsync(index, new byte[10]);

        Assert.Equal(ChunkWriteStatus.Rejected, result.Status);
        Assert.Contains(expectedMessage, result.Message);
    }

    [Theory]
    [InlineData(0, Chunk - 1)]
    [InlineData(1, 9)]
    [InlineData(1, 11)]
    public async Task ChunkWithWrongLength_ShouldBeRejected(int index, int length)
    {
        var store = NewStore();
        var payload = Payload(Chunk + 10);
        await using var session = store.Create(7, payload.Length);
        var reading = ReadAllAsync(session.Stream);
        if (index == 1)
        {
            await session.WriteChunkAsync(0, payload.AsMemory(0, Chunk));
        }

        var result = await session.WriteChunkAsync(index, new byte[length]);

        Assert.Equal(ChunkWriteStatus.Rejected, result.Status);
        Assert.Contains("位元組", result.Message);
        GC.KeepAlive(reading);
    }

    [Fact]
    public async Task TryGet_ShouldOnlyFindOwnSessions()
    {
        // 別人的工作階段要回「找不到」，不能讓人分得出它存在。
        var store = NewStore();
        await using var session = store.Create(ownerUserId: 7, 10);

        Assert.True(store.TryGet(session.Id, 7, out var found));
        Assert.Same(session, found);
        Assert.False(store.TryGet(session.Id, 8, out _));
        Assert.False(store.TryGet(Guid.NewGuid(), 7, out _));
    }

    [Fact]
    public async Task DisposingTheReader_ShouldStopFurtherChunks_AndRemoveTheSession()
    {
        // 服務層讀完、拒絕或取消後就釋放工作階段；瀏覽器下一塊要拿到「已停止」才會收手。
        var store = NewStore();
        var session = store.Create(7, Chunk * 3);

        await session.DisposeAsync();
        var result = await session.WriteChunkAsync(0, new byte[Chunk]);

        Assert.Equal(ChunkWriteStatus.Aborted, result.Status);
        Assert.False(store.TryGet(session.Id, 7, out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ReaderStoppingMidway_ShouldAbortTheNextChunk()
    {
        var store = NewStore();
        var payload = Payload(Chunk * 3);
        var session = store.Create(7, payload.Length);

        var buffer = new byte[100];
        var write = session.WriteChunkAsync(0, payload.AsMemory(0, Chunk));
        _ = await session.Stream.ReadAsync(buffer);
        await write;
        await session.DisposeAsync();

        var next = await session.WriteChunkAsync(1, payload.AsMemory(Chunk, Chunk));
        Assert.Equal(ChunkWriteStatus.Aborted, next.Status);
    }

    [Fact]
    public async Task Fail_ShouldSurfaceAsIOException_ToTheReader()
    {
        // 瀏覽器放棄（重試用完、登入失效）時，服務層要像讀檔失敗一樣走「上傳失敗」。
        var store = NewStore();
        await using var session = store.Create(7, Chunk * 2);
        await session.WriteChunkAsync(0, new byte[Chunk]);

        session.Fail("網路斷線");

        var ex = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(session.Stream));
        Assert.Equal("網路斷線", ex.Message);
        Assert.False(store.TryGet(session.Id, 7, out _));
    }

    [Fact]
    public async Task Fail_WhileAWriteIsWaitingForTheReader_ShouldNotDeadlock()
    {
        // 讀取端停住時寫入端會卡在背壓；逾時中止要能把它叫醒，不能互等。
        var store = NewStore();
        await using var session = store.Create(7, Chunk * 4L);
        await session.WriteChunkAsync(0, new byte[Chunk]);
        var blocked = session.WriteChunkAsync(1, new byte[Chunk]);
        Assert.False(blocked.IsCompleted);

        var failing = Task.Run(() => session.Fail("逾時"));

        Assert.Equal(failing, await Task.WhenAny(failing, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(ChunkWriteStatus.Aborted, (await blocked).Status);
    }

    [Fact]
    public async Task IdleTimeout_ShouldFailTheUpload()
    {
        // 使用者關掉分頁之後不會再有分塊進來，讀取端不能永遠等下去。
        var store = new ChunkedUploadStore(NullLogger<ChunkedUploadStore>.Instance, TimeSpan.FromMilliseconds(100));
        await using var session = store.Create(7, Chunk * 2);

        var ex = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(session.Stream));

        Assert.Equal(ChunkedUploadStore.IdleTimeoutMessage, ex.Message);
    }

    [Fact]
    public async Task EmptyFile_ShouldReadAsEmptyStream_WithoutAnyChunk()
    {
        var store = NewStore();
        await using var session = store.Create(7, 0);

        Assert.Equal(0, session.ChunkCount);
        Assert.Empty(await ReadAllAsync(session.Stream));
    }

    private static ChunkedUploadStore NewStore() => new(NullLogger<ChunkedUploadStore>.Instance);

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
