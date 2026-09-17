using System.Threading.Channels;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 待轉錄會議的行程內佇列。
///
/// <para>
/// 刻意不做持久化：本專案是單一實例假設（SQLite 檔案資料庫 + 本機磁碟儲存），
/// 行程內 <see cref="Channel{T}"/> 已足夠。應用程式重啟後未完成的項目不會自動重跑，
/// 改由使用者在畫面上按「重新轉錄」重新入列（啟動時會把殘留的「待處理」與「處理中」一律標記為失敗）。
/// </para>
/// </summary>
public interface ITranscriptionQueue
{
    /// <summary>將會議排入轉錄佇列。</summary>
    ValueTask EnqueueAsync(MeetingJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>取出下一筆待轉錄的會議 Id（無資料時等待）。</summary>
    ValueTask<MeetingJobRequest> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class TranscriptionQueue : ITranscriptionQueue
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
