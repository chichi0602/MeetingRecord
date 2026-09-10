using AntDesign;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右下角常駐的背景工作進度面板（形式比照雲端硬碟的上傳進度）。
///
/// <para>
/// 掛在 <c>MainLayout</c>，所以切到任何頁面都看得到。同時顯示語音轉錄與會議紀錄生成
/// 兩種工作——兩者都固定在右下角，各做一個面板會直接重疊，因此合併成同一個。
/// 資料來自兩個 Singleton 通知器，不查資料庫。
/// </para>
/// </summary>
public partial class BackgroundJobProgressPanel : ComponentBase, IDisposable
{
    [Inject]
    public ITranscriptionProgressNotifier TranscriptionNotifier { get; set; } = default!;

    [Inject]
    public IMeetingDraftProgressNotifier DraftNotifier { get; set; } = default!;

    [Inject]
    public IRecordAccessScopeProvider AccessScope { get; set; } = default!;

    [Inject]
    public IJobCancellationRegistry CancellationRegistry { get; set; } = default!;

    [Inject]
    public ILogger<BackgroundJobProgressPanel> Logger { get; set; } = default!;

    private List<JobRow> visibleJobs = [];
    private bool isCollapsed;

    private string HeaderText
    {
        get
        {
            var running = visibleJobs.Count(x => x.IsRunning);
            return running > 0 ? $"處理中（{running}）" : "已完成";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        TranscriptionNotifier.Changed += OnProgressChanged;
        DraftNotifier.Changed += OnProgressChanged;
        await RefreshAsync();
    }

    /// <summary>
    /// 由背景執行緒引發，必須切回 UI 執行緒才能碰元件狀態。
    /// </summary>
    private void OnProgressChanged()
        => _ = InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        });

    /// <summary>
    /// 重新取兩邊的快照、轉成畫面用的列，並套用團隊可見性。
    ///
    /// <para>
    /// 通知器是全行程共用的，所以這裡要過濾——否則會把受團隊限制的會議標題
    /// 洩漏給看不到那筆資料的使用者。
    /// </para>
    /// </summary>
    private async Task RefreshAsync()
    {
        var scope = await AccessScope.GetAsync();

        var transcriptionJobs = TranscriptionNotifier
            .GetSnapshot()
            .Where(x => TagStringHelper.IsTeamAccessible(x.Teams, scope.Teams, scope.IsAdmin))
            .Select(JobRow.FromTranscription);

        var draftJobs = DraftNotifier
            .GetSnapshot()
            .Where(x => TagStringHelper.IsTeamAccessible(x.Teams, scope.Teams, scope.IsAdmin))
            .Select(JobRow.FromDraft);

        // 進行中的排前面，讓仍在跑的工作不會被一堆已完成的項目擠下去。
        visibleJobs = [.. transcriptionJobs
            .Concat(draftJobs)
            .OrderByDescending(x => x.IsRunning)
            .ThenBy(x => x.Title)];
    }

    private void ToggleCollapsed() => isCollapsed = !isCollapsed;

    /// <summary>
    /// 關閉鈕的說明。進行中時必須講清楚**工作會繼續跑**——
    /// 這顆按鈕在 0.4.55 之前只寫「關閉這一筆」，很容易被當成取消。
    /// </summary>
    private static string CloseButtonLabel(JobRow job)
        => job.IsRunning ? "關閉通知（工作會繼續執行）" : "關閉這一筆";

    /// <summary>
    /// 真正取消工作。排隊中的會在 worker 取出時被跳過，執行中的會直接中斷。
    /// </summary>
    private void CancelJob(JobRow job)
    {
        var kind = job.Kind == JobKind.Transcription
            ? BackgroundJobKind.Transcription
            : BackgroundJobKind.MeetingDraft;

        CancellationRegistry.RequestCancel(kind, job.MeetingId);

        Logger.LogInformation(
            "Background job cancellation requested. Kind={Kind}, MeetingId={MeetingId}",
            kind,
            job.MeetingId);
    }

    private void Dismiss(JobRow job)
    {
        if (job.Kind == JobKind.Transcription)
        {
            TranscriptionNotifier.Dismiss(job.MeetingId);
        }
        else
        {
            DraftNotifier.Dismiss(job.MeetingId);
        }
    }

    private void DismissAll()
    {
        foreach (var job in visibleJobs)
        {
            Dismiss(job);
        }
    }

    public void Dispose()
    {
        TranscriptionNotifier.Changed -= OnProgressChanged;
        DraftNotifier.Changed -= OnProgressChanged;
    }

    private enum JobKind
    {
        Transcription,
        Draft,
    }

    /// <summary>
    /// 面板顯示用的一列。把兩個通知器各自的項目型別統一成同一種形狀，
    /// 這樣就不必為了共用面板去動 Business 層的兩個獨立通知器。
    /// </summary>
    private sealed record JobRow(
        JobKind Kind,
        int MeetingId,
        string Title,
        int Percent,
        string PhaseText,
        bool IsRunning,
        bool IsCompleted,
        bool IsFailed,
        string? ErrorMessage)
    {
        /// <summary>同一筆會議可能同時有轉錄與生成兩種工作，所以 key 要帶上種類。</summary>
        public string Key => $"{Kind}-{MeetingId}";

        public string KindText => Kind == JobKind.Transcription ? "語音轉文字" : "產生會議紀錄";

        public string KindIcon => Kind == JobKind.Transcription ? "graphic_eq" : "auto_awesome";

        public ProgressStatus ProgressStatus => IsCompleted
            ? AntDesign.ProgressStatus.Success
            : IsFailed
                ? AntDesign.ProgressStatus.Exception
                : AntDesign.ProgressStatus.Active;

        public static JobRow FromTranscription(TranscriptionProgressItem item) => new(
            JobKind.Transcription,
            item.MeetingId,
            item.Title,
            item.Percent,
            DescribeTranscriptionPhase(item),
            item.IsRunning,
            item.Phase == TranscriptionPhase.Completed,
            item.Phase == TranscriptionPhase.Failed,
            item.ErrorMessage);

        public static JobRow FromDraft(MeetingDraftProgressItem item) => new(
            JobKind.Draft,
            item.MeetingId,
            item.Title,
            item.Percent,
            DescribeDraftPhase(item),
            item.IsRunning,
            item.Phase == MeetingDraftPhase.Completed,
            item.Phase == MeetingDraftPhase.Failed,
            item.ErrorMessage);

        private static string DescribeTranscriptionPhase(TranscriptionProgressItem item) => item.Phase switch
        {
            TranscriptionPhase.Queued => "排隊中",
            TranscriptionPhase.Converting => "轉檔中",
            TranscriptionPhase.Transcribing => $"轉錄中（第 {item.CompletedSegments}/{item.TotalSegments} 段）",
            TranscriptionPhase.Completed => "已完成",
            TranscriptionPhase.Failed => "失敗",
            _ => string.Empty,
        };

        private static string DescribeDraftPhase(MeetingDraftProgressItem item) => item.Phase switch
        {
            MeetingDraftPhase.Queued => "排隊中",
            MeetingDraftPhase.Preparing => "讀取逐字稿與提示詞",
            MeetingDraftPhase.Summarizing => $"分段摘要中（第 {item.CompletedChunks}/{item.TotalChunks} 段）",
            MeetingDraftPhase.Generating => $"產生會議紀錄中（已產生 {item.GeneratedCharacters} 字）",
            MeetingDraftPhase.Completed => "已完成",
            MeetingDraftPhase.Failed => "失敗",
            _ => string.Empty,
        };
    }
}
