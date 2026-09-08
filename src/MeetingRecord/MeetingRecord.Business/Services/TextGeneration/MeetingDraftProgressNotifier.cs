using System.Collections.Concurrent;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>會議紀錄草稿生成目前所處的階段。</summary>
public enum MeetingDraftPhase
{
    /// <summary>已排入佇列，還沒輪到。</summary>
    Queued = 0,

    /// <summary>讀取提示詞範本與逐字稿。</summary>
    Preparing = 1,

    /// <summary>逐段摘要中（map 階段，只有長逐字稿會走）。</summary>
    Summarizing = 2,

    /// <summary>產生會議紀錄中（reduce 階段的單次 LLM 呼叫）。</summary>
    Generating = 3,

    /// <summary>全部完成。</summary>
    Completed = 4,

    /// <summary>失敗。</summary>
    Failed = 5,
}

/// <summary>單一草稿生成工作的即時進度快照。</summary>
/// <param name="MeetingId">會議 Id。</param>
/// <param name="Title">會議標題，用於畫面顯示。</param>
/// <param name="Teams">會議的團隊標籤原始字串，供畫面做可見性過濾。</param>
/// <param name="Phase">目前階段。</param>
/// <param name="CompletedChunks">已完成摘要的段數。</param>
/// <param name="TotalChunks">總段數（未分段時為 0）。</param>
/// <param name="GeneratedCharacters">最終生成階段目前累計已產生的字元數。</param>
/// <param name="ErrorMessage">失敗訊息。</param>
/// <param name="CompletedAt">完成或失敗的時間。</param>
public sealed record MeetingDraftProgressItem(
    int MeetingId,
    string Title,
    string? Teams,
    MeetingDraftPhase Phase,
    int CompletedChunks,
    int TotalChunks,
    int GeneratedCharacters,
    string? ErrorMessage,
    DateTime? CompletedAt)
{
    /// <summary>目前進度百分比。</summary>
    public int Percent => MeetingDraftProgressNotifier.CalculatePercent(
        Phase,
        CompletedChunks,
        TotalChunks,
        GeneratedCharacters);

    /// <summary>是否仍在進行中。</summary>
    public bool IsRunning => Phase is MeetingDraftPhase.Queued
        or MeetingDraftPhase.Preparing
        or MeetingDraftPhase.Summarizing
        or MeetingDraftPhase.Generating;
}

/// <summary>
/// 會議紀錄草稿生成進度的行程內通知器，形狀比照
/// <see cref="Transcription.ITranscriptionProgressNotifier"/>。
///
/// <para>
/// 同樣刻意只放記憶體、不進資料庫：進度只在工作進行中有意義，通知器是 Singleton
/// 所以跨 circuit 與頁面重新整理都還在；<c>Program.cs</c> 啟動時已會把殘留的
/// <c>DraftStatus.Processing</c> 標記為失敗，不會留下卡住的百分比。最終狀態本來就存在
/// <c>Meeting.DraftStatus</c>。
/// </para>
///
/// <para>
/// <see cref="Changed"/> 由背景執行緒引發，訂閱端必須自行以
/// <c>InvokeAsync(StateHasChanged)</c> 切回 UI 執行緒，並在 <c>Dispose</c> 解除訂閱。
/// </para>
/// </summary>
public interface IMeetingDraftProgressNotifier
{
    /// <summary>任一工作的進度有變動時引發。</summary>
    event Action? Changed;

    /// <summary>取得目前所有工作的快照（含已完成但尚未關閉的項目）。</summary>
    IReadOnlyList<MeetingDraftProgressItem> GetSnapshot();

    /// <summary>取得單一會議的進度；沒有進行中的工作時回傳 null。</summary>
    MeetingDraftProgressItem? Find(int meetingId);

    /// <summary>會議排入佇列。同一個 Id 重新入列（重新產生）會覆蓋舊項目。</summary>
    void Enqueued(int meetingId, string title, string? teams);

    /// <summary>開始讀取提示詞範本與逐字稿。</summary>
    void ReportPreparing(int meetingId);

    /// <summary>回報已完成摘要的段數。</summary>
    void ReportSummarizing(int meetingId, int completedChunks, int totalChunks);

    /// <summary>進入最終生成階段。</summary>
    void ReportGenerating(int meetingId);

    /// <summary>回報最終生成階段目前累計已產生的字元數（來自 LLM 串流）。</summary>
    void ReportGeneratedCharacters(int meetingId, int characters);

    /// <summary>生成完成。</summary>
    void ReportCompleted(int meetingId);

    /// <summary>生成失敗。</summary>
    void ReportFailed(int meetingId, string message);

    /// <summary>把項目從清單移除（使用者關閉通知）。</summary>
    void Dismiss(int meetingId);
}

public sealed class MeetingDraftProgressNotifier : IMeetingDraftProgressNotifier
{
    /// <summary>準備階段（讀範本與逐字稿）在整體進度中佔的比重。</summary>
    public const int PreparingPercent = 5;

    /// <summary>進入最終生成時的進度。分段摘要佔 <see cref="PreparingPercent"/> 到這個值之間。</summary>
    public const int GeneratingPercent = 70;

    /// <summary>
    /// 最終生成階段的進度上限。刻意停在 99——100% 只有 <see cref="MeetingDraftPhase.Completed"/>
    /// 給得出來，避免出現「先衝到 100 再乾等」這種失信的畫面。
    /// </summary>
    public const int MaxGeneratingPercent = 99;

    /// <summary>
    /// 生成曲線的半衰字元數：產出這麼多字時，剛好走完
    /// <see cref="GeneratingPercent"/> 到 <see cref="MaxGeneratingPercent"/> 之間的一半。
    /// 取 900 是因為一般會議紀錄約落在 800～2500 字。
    /// </summary>
    public const int GeneratingCharacterHalfLife = 900;

    private readonly ConcurrentDictionary<int, MeetingDraftProgressItem> items = new();

    public event Action? Changed;

    public IReadOnlyList<MeetingDraftProgressItem> GetSnapshot()
        => [.. items.Values.OrderBy(x => x.MeetingId)];

    public MeetingDraftProgressItem? Find(int meetingId)
        => items.TryGetValue(meetingId, out var item) ? item : null;

    public void Enqueued(int meetingId, string title, string? teams)
    {
        items[meetingId] = new MeetingDraftProgressItem(
            meetingId,
            title,
            teams,
            MeetingDraftPhase.Queued,
            CompletedChunks: 0,
            TotalChunks: 0,
            GeneratedCharacters: 0,
            ErrorMessage: null,
            CompletedAt: null);

        Notify();
    }

    public void ReportPreparing(int meetingId)
        => Update(meetingId, item => item with
        {
            Phase = MeetingDraftPhase.Preparing,
            CompletedChunks = 0,
            TotalChunks = 0,
            GeneratedCharacters = 0,
        });

    public void ReportSummarizing(int meetingId, int completedChunks, int totalChunks)
        => Update(meetingId, item => item with
        {
            Phase = MeetingDraftPhase.Summarizing,
            CompletedChunks = completedChunks,
            TotalChunks = totalChunks,
        });

    public void ReportGenerating(int meetingId)
        => Update(meetingId, item => item with
        {
            Phase = MeetingDraftPhase.Generating,
            GeneratedCharacters = 0,
        });

    /// <summary>
    /// 回報串流已產生的字元數。
    ///
    /// <para>
    /// 每個 SSE 片段只有幾個字，逐片引發 <see cref="Changed"/> 會用上百次重繪轟炸 Blazor circuit。
    /// 因此字典一律更新（畫面下次讀到的就是最新值），但**只有換算出的整數百分比真的變了才通知**——
    /// 生成階段只有 70～99 這 30 個值，整段生成最多引發 29 次事件。
    /// </para>
    /// </summary>
    public void ReportGeneratedCharacters(int meetingId, int characters)
    {
        if (!items.TryGetValue(meetingId, out var current))
        {
            return;
        }

        var updated = current with { GeneratedCharacters = characters };
        items[meetingId] = updated;

        if (updated.Percent != current.Percent)
        {
            Notify();
        }
    }

    public void ReportCompleted(int meetingId)
        => Update(meetingId, item => item with
        {
            Phase = MeetingDraftPhase.Completed,
            ErrorMessage = null,
            CompletedAt = DateTime.Now,
        });

    public void ReportFailed(int meetingId, string message)
        => Update(meetingId, item => item with
        {
            Phase = MeetingDraftPhase.Failed,
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
    /// 依階段、段數與已產生字元數換算百分比。
    ///
    /// <para>
    /// 逐字稿要超過 <c>TranscriptChunker.DefaultMaxChars</c>（12000 字元，約一小時以上的會議）
    /// 才會分段，所以**多數會議根本沒有 Summarizing 階段**，整段時間都耗在最終那次 LLM 呼叫上。
    /// 0.4.42 之前那次呼叫是非串流的，中途拿不到任何訊號，畫面只能停在 70% 直到完成；
    /// 現在改成串流，進度改由「已產生多少字」推動——是真訊號，不是拿經過時間內插的假動畫。
    /// </para>
    /// </summary>
    public static int CalculatePercent(
        MeetingDraftPhase phase,
        int completedChunks,
        int totalChunks,
        int generatedCharacters = 0)
    {
        switch (phase)
        {
            case MeetingDraftPhase.Queued:
                return 0;

            case MeetingDraftPhase.Preparing:
                return PreparingPercent;

            case MeetingDraftPhase.Generating:
                return GeneratingPercentFor(generatedCharacters);

            case MeetingDraftPhase.Completed:
                return 100;

            case MeetingDraftPhase.Summarizing when totalChunks > 0:
                return SummarizingPercent(completedChunks, totalChunks);

            case MeetingDraftPhase.Summarizing:
                // 還沒拿到總段數時，維持準備階段的比重。
                return PreparingPercent;

            case MeetingDraftPhase.Failed when generatedCharacters > 0:
                // 失敗時停在中斷當下的進度，讓使用者看得出是哪個階段掛的。
                return GeneratingPercentFor(generatedCharacters);

            case MeetingDraftPhase.Failed:
                return totalChunks > 0 ? SummarizingPercent(completedChunks, totalChunks) : PreparingPercent;

            default:
                return 0;
        }
    }

    private static int SummarizingPercent(int completedChunks, int totalChunks)
    {
        var done = Math.Clamp(completedChunks, 0, totalChunks);
        return PreparingPercent + ((GeneratingPercent - PreparingPercent) * done / totalChunks);
    }

    /// <summary>
    /// 最終生成階段的漸近曲線：<c>70 + 29 × 字數 ÷ (字數 + 900)</c>。
    ///
    /// <para>
    /// 用漸近而非線性，是因為**沒有人知道模型最後會產出多少字**，沒有分母可以做線性內插。
    /// 漸近曲線只需要「已產生字數」這一個已知量，就能保證單調遞增、一開始爬得快、
    /// 而且永遠到不了 100%。
    /// </para>
    /// </summary>
    private static int GeneratingPercentFor(int generatedCharacters)
    {
        long characters = Math.Max(generatedCharacters, 0);
        long span = MaxGeneratingPercent - GeneratingPercent;

        // 用 long 運算：字數逼近 int.MaxValue 時，span * characters 與 characters + 半衰值都會溢位成負數。
        return GeneratingPercent + (int)(span * characters / (characters + GeneratingCharacterHalfLife));
    }

    /// <summary>
    /// 更新既有項目。找不到項目時什麼都不做——代表這筆不是從 <see cref="Enqueued"/> 進來的
    /// （例如應用程式重啟後才輪到的殘留工作），不必憑空補一筆沒有標題的項目。
    /// </summary>
    private void Update(int meetingId, Func<MeetingDraftProgressItem, MeetingDraftProgressItem> mutate)
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
