using System.Collections.Concurrent;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>轉錄工作目前所處的階段。</summary>
public enum TranscriptionPhase
{
    /// <summary>已排入佇列，還沒輪到。</summary>
    Queued = 0,

    /// <summary>ffmpeg 轉檔切段中。</summary>
    Converting = 1,

    /// <summary>逐段送交語音辨識中。</summary>
    Transcribing = 2,

    /// <summary>全部完成。</summary>
    Completed = 3,

    /// <summary>失敗。</summary>
    Failed = 4,
}

/// <summary>單一轉錄工作的即時進度快照。</summary>
/// <param name="MeetingId">會議 Id。</param>
/// <param name="Title">會議標題，用於畫面顯示。</param>
/// <param name="Teams">會議的團隊標籤原始字串，供畫面做可見性過濾。</param>
/// <param name="Phase">目前階段。</param>
/// <param name="CompletedSegments">已完成的段數。</param>
/// <param name="TotalSegments">總段數（轉檔完成前為 0）。</param>
/// <param name="ErrorMessage">失敗訊息。</param>
/// <param name="CompletedAt">完成或失敗的時間。</param>
public sealed record TranscriptionProgressItem(
    int MeetingId,
    string Title,
    string? Teams,
    TranscriptionPhase Phase,
    int CompletedSegments,
    int TotalSegments,
    string? ErrorMessage,
    DateTime? CompletedAt)
{
    /// <summary>目前進度百分比。</summary>
    public int Percent => TranscriptionProgressNotifier.CalculatePercent(Phase, CompletedSegments, TotalSegments);

    /// <summary>是否仍在進行中（排隊、轉檔或轉錄）。</summary>
    public bool IsRunning => Phase is TranscriptionPhase.Queued or TranscriptionPhase.Converting or TranscriptionPhase.Transcribing;
}

/// <summary>
/// 轉錄進度的行程內通知器。
///
/// <para>
/// 刻意只放在記憶體、不進資料庫：進度只在工作進行中有意義，而通知器是 Singleton，
/// 跨 circuit、跨頁面重新整理都還在；應用程式重啟時 <c>Program.cs</c> 已會把殘留的
/// 「處理中」標記為失敗，所以不會留下卡住的百分比。最終狀態本來就存在
/// <c>Meeting.TranscriptionStatus</c>，這裡不重複保存。
/// </para>
///
/// <para>
/// <see cref="Changed"/> 由背景執行緒引發，訂閱端（Blazor 元件）必須自行以
/// <c>InvokeAsync(StateHasChanged)</c> 切回 UI 執行緒，並在 <c>Dispose</c> 解除訂閱。
/// </para>
/// </summary>
public interface ITranscriptionProgressNotifier
{
    /// <summary>任一工作的進度有變動時引發。</summary>
    event Action? Changed;

    /// <summary>取得目前所有工作的快照（含已完成但尚未關閉的項目）。</summary>
    IReadOnlyList<TranscriptionProgressItem> GetSnapshot();

    /// <summary>取得單一會議的進度；沒有進行中的工作時回傳 null。</summary>
    TranscriptionProgressItem? Find(int meetingId);

    /// <summary>會議排入佇列。同一個 Id 重新入列（重新轉錄）會覆蓋舊項目。</summary>
    void Enqueued(int meetingId, string title, string? teams);

    /// <summary>開始 ffmpeg 轉檔切段。</summary>
    void ReportConverting(int meetingId);

    /// <summary>回報已完成的段數。</summary>
    void ReportSegment(int meetingId, int completedSegments, int totalSegments);

    /// <summary>轉錄完成。</summary>
    void ReportCompleted(int meetingId);

    /// <summary>轉錄失敗。</summary>
    void ReportFailed(int meetingId, string message);

    /// <summary>把項目從清單移除（使用者關閉通知）。</summary>
    void Dismiss(int meetingId);
}

public sealed class TranscriptionProgressNotifier : ITranscriptionProgressNotifier
{
    /// <summary>轉檔階段在整體進度中佔的比重。轉檔沒有進度回呼，只能給一個固定值避免卡在 0%。</summary>
    public const int ConvertingPercent = 5;

    private readonly ConcurrentDictionary<int, TranscriptionProgressItem> items = new();

    public event Action? Changed;

    public IReadOnlyList<TranscriptionProgressItem> GetSnapshot()
        => [.. items.Values.OrderBy(x => x.MeetingId)];

    public TranscriptionProgressItem? Find(int meetingId)
        => items.TryGetValue(meetingId, out var item) ? item : null;

    public void Enqueued(int meetingId, string title, string? teams)
    {
        items[meetingId] = new TranscriptionProgressItem(
            meetingId,
            title,
            teams,
            TranscriptionPhase.Queued,
            CompletedSegments: 0,
            TotalSegments: 0,
            ErrorMessage: null,
            CompletedAt: null);

        Notify();
    }

    public void ReportConverting(int meetingId)
        => Update(meetingId, item => item with
        {
            Phase = TranscriptionPhase.Converting,
            CompletedSegments = 0,
            TotalSegments = 0,
        });

    public void ReportSegment(int meetingId, int completedSegments, int totalSegments)
        => Update(meetingId, item => item with
        {
            Phase = TranscriptionPhase.Transcribing,
            CompletedSegments = completedSegments,
            TotalSegments = totalSegments,
        });

    public void ReportCompleted(int meetingId)
        => Update(meetingId, item => item with
        {
            Phase = TranscriptionPhase.Completed,
            ErrorMessage = null,
            CompletedAt = DateTime.Now,
        });

    public void ReportFailed(int meetingId, string message)
        => Update(meetingId, item => item with
        {
            Phase = TranscriptionPhase.Failed,
            ErrorMessage = message,
            CompletedAt = DateTime.Now,
        });

    public void Dismiss(int meetingId)
    {
        if (items.TryRemove(meetingId, out _))
        {
            Notify();
        }
    }

    /// <summary>
    /// 依階段與段數換算百分比。
    /// 轉檔沒有進度回呼，因此獨立佔 <see cref="ConvertingPercent"/>，剩下的才按段數分配——
    /// 否則短音檔（只有一段）在整段轉錄期間都會停在 0%。
    /// </summary>
    public static int CalculatePercent(TranscriptionPhase phase, int completedSegments, int totalSegments)
    {
        switch (phase)
        {
            case TranscriptionPhase.Queued:
                return 0;

            case TranscriptionPhase.Converting:
                return ConvertingPercent;

            case TranscriptionPhase.Completed:
                return 100;

            case TranscriptionPhase.Transcribing when totalSegments > 0:
                var done = Math.Clamp(completedSegments, 0, totalSegments);
                return ConvertingPercent + ((100 - ConvertingPercent) * done / totalSegments);

            case TranscriptionPhase.Transcribing:
                // 還沒拿到總段數時，維持轉檔完成的比重。
                return ConvertingPercent;

            case TranscriptionPhase.Failed:
                // 失敗時停在中斷當下的進度，讓使用者看得出是哪個階段掛的。
                return totalSegments > 0
                    ? ConvertingPercent + ((100 - ConvertingPercent) * Math.Clamp(completedSegments, 0, totalSegments) / totalSegments)
                    : ConvertingPercent;

            default:
                return 0;
        }
    }

    /// <summary>
    /// 更新既有項目。找不到項目時什麼都不做——代表這筆不是從 <see cref="Enqueued"/> 進來的
    /// （例如應用程式重啟後才輪到的殘留工作），不必憑空補一筆沒有標題的項目。
    /// </summary>
    private void Update(int meetingId, Func<TranscriptionProgressItem, TranscriptionProgressItem> mutate)
    {
        if (!items.TryGetValue(meetingId, out var current))
        {
            return;
        }

        items[meetingId] = mutate(current);
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
