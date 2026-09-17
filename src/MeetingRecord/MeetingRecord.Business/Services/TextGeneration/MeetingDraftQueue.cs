using System.Threading.Channels;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 會議紀錄草稿生成的待處理佇列。
///
/// 與轉錄佇列刻意分開而不共用：轉錄一筆可能跑數十分鐘，若共用單一 worker，
/// 只要有一筆長音檔在轉錄，所有草稿生成都會被堵在後面。
///
/// 同樣刻意不做持久化（單一實例假設）；重啟後殘留的「生成中」由 Program.cs
/// 啟動時改判為「失敗」，使用者可再次觸發。
/// </summary>
public interface IMeetingDraftQueue
{
    ValueTask EnqueueAsync(MeetingJobRequest request, CancellationToken cancellationToken = default);

    ValueTask<MeetingJobRequest> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class MeetingDraftQueue : IMeetingDraftQueue
{
    private readonly Channel<MeetingJobRequest> channel = Channel.CreateUnbounded<MeetingJobRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(MeetingJobRequest request, CancellationToken cancellationToken = default)
        => channel.Writer.WriteAsync(request, cancellationToken);

    public ValueTask<MeetingJobRequest> DequeueAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAsync(cancellationToken);
}
