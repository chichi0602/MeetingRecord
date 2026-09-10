using System.Collections.Concurrent;

namespace MeetingRecord.Business.Services.Other;

/// <summary>可取消的背景工作種類。</summary>
public enum BackgroundJobKind
{
    /// <summary>語音轉文字。</summary>
    Transcription = 0,

    /// <summary>AI 會議紀錄生成。</summary>
    MeetingDraft = 1,
}

/// <summary>
/// 一次工作執行期間的取消控制權杖。
///
/// <para>
/// <see cref="IsCancelledByUser"/> 是關鍵——沒有它就**無法把「使用者按了取消」與
/// 「應用程式正在關機」區分開**，兩者都只會拋出 <see cref="OperationCanceledException"/>，
/// 於是關機時所有進行中的工作都會被誤標成「已取消」。
/// </para>
/// </summary>
public sealed class JobCancellationHandle : IDisposable
{
    private readonly CancellationTokenSource linkedSource;
    private readonly Action onDispose;

    internal JobCancellationHandle(CancellationTokenSource linkedSource, Action onDispose)
    {
        this.linkedSource = linkedSource;
        this.onDispose = onDispose;
    }

    /// <summary>與服務停止權杖連動的權杖，傳給實際執行工作的程式。</summary>
    public CancellationToken Token => linkedSource.Token;

    /// <summary>這次中斷是不是使用者主動要求的。</summary>
    public bool IsCancelledByUser { get; private set; }

    internal void CancelByUser()
    {
        IsCancelledByUser = true;

        // 已經跑完並 Dispose 的工作仍可能收到取消請求（使用者手快），這裡不該炸掉。
        try
        {
            linkedSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        onDispose();
        linkedSource.Dispose();
    }
}

/// <summary>
/// 背景工作的取消登記處。轉錄與草稿生成共用同一份。
///
/// <para>
/// 必須是 Singleton：發出取消的是 Blazor circuit（Scoped），執行工作的是背景服務（Singleton），
/// 兩者要透過同一個實例才碰得到面。
/// </para>
/// </summary>
public interface IJobCancellationRegistry
{
    /// <summary>
    /// 要求取消。工作正在執行就直接中斷；還在排隊就記下來，等 worker 取出時跳過。
    /// </summary>
    void RequestCancel(BackgroundJobKind kind, int meetingId);

    /// <summary>
    /// 取出待處理的取消標記並清除。worker 從佇列拿到工作後、真正開始前呼叫。
    /// **只會成立一次**——否則同一筆重新入列後會被上一次的標記誤殺。
    /// </summary>
    bool TryConsumePendingCancel(BackgroundJobKind kind, int meetingId);

    /// <summary>登記一次執行，回傳可取消的權杖。用完必須 Dispose。</summary>
    JobCancellationHandle BeginJob(BackgroundJobKind kind, int meetingId, CancellationToken serviceToken);
}

public sealed class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<(BackgroundJobKind Kind, int MeetingId), JobCancellationHandle> running = new();
    private readonly ConcurrentDictionary<(BackgroundJobKind Kind, int MeetingId), byte> pendingCancels = new();

    public void RequestCancel(BackgroundJobKind kind, int meetingId)
    {
        var key = (kind, meetingId);

        if (running.TryGetValue(key, out var handle))
        {
            handle.CancelByUser();
            return;
        }

        // 還在排隊（或剛好在登記前的空檔）：留下標記，由 worker 取出時處理。
        pendingCancels[key] = 0;
    }

    public bool TryConsumePendingCancel(BackgroundJobKind kind, int meetingId)
        => pendingCancels.TryRemove((kind, meetingId), out _);

    public JobCancellationHandle BeginJob(BackgroundJobKind kind, int meetingId, CancellationToken serviceToken)
    {
        var key = (kind, meetingId);

        // 登記前先清掉殘留的標記：走到這裡代表 worker 已經檢查過並決定要執行，
        // 留著會讓下一次入列被誤殺。
        pendingCancels.TryRemove(key, out _);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        var handle = new JobCancellationHandle(linked, () => running.TryRemove(key, out _));

        running[key] = handle;

        return handle;
    }
}
