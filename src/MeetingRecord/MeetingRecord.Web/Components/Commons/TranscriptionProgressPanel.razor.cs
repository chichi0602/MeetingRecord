using AntDesign;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右下角常駐的轉錄進度面板（形式比照雲端硬碟的上傳進度）。
///
/// <para>
/// 掛在 <c>MainLayout</c>，所以切到任何頁面都看得到。資料來自 Singleton 的
/// <see cref="ITranscriptionProgressNotifier"/>，不查資料庫。
/// </para>
/// </summary>
public partial class TranscriptionProgressPanel : ComponentBase, IDisposable
{
    [Inject]
    public ITranscriptionProgressNotifier ProgressNotifier { get; set; } = default!;

    [Inject]
    public IRecordAccessScopeProvider AccessScope { get; set; } = default!;

    private List<TranscriptionProgressItem> visibleItems = [];
    private bool isCollapsed;

    private string HeaderText
    {
        get
        {
            var running = visibleItems.Count(x => x.IsRunning);
            return running > 0 ? $"轉錄中（{running}）" : "轉錄完成";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        ProgressNotifier.Changed += OnProgressChanged;
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
    /// 重新取快照並套用團隊可見性。
    ///
    /// <para>
    /// 通知器是全行程共用的，所以這裡要過濾——否則會把受團隊限制的會議標題
    /// 洩漏給看不到那筆資料的使用者。0.4.35 之後新建的會議沒有團隊（視為公開），
    /// 這道過濾主要是保護既有的舊資料。
    /// </para>
    /// </summary>
    private async Task RefreshAsync()
    {
        var scope = await AccessScope.GetAsync();

        visibleItems = [.. ProgressNotifier
            .GetSnapshot()
            .Where(x => TagStringHelper.IsTeamAccessible(x.Teams, scope.Teams, scope.IsAdmin))];
    }

    private void ToggleCollapsed() => isCollapsed = !isCollapsed;

    private void Dismiss(int meetingId) => ProgressNotifier.Dismiss(meetingId);

    private void DismissAll()
    {
        foreach (var item in visibleItems)
        {
            ProgressNotifier.Dismiss(item.MeetingId);
        }
    }

    private static ProgressStatus ResolveProgressStatus(TranscriptionProgressItem item) => item.Phase switch
    {
        TranscriptionPhase.Completed => ProgressStatus.Success,
        TranscriptionPhase.Failed => ProgressStatus.Exception,
        _ => ProgressStatus.Active,
    };

    private static string DescribePhase(TranscriptionProgressItem item) => item.Phase switch
    {
        TranscriptionPhase.Queued => "排隊中",
        TranscriptionPhase.Converting => "轉檔中",
        TranscriptionPhase.Transcribing => $"轉錄中（第 {item.CompletedSegments}/{item.TotalSegments} 段）",
        TranscriptionPhase.Completed => "已完成",
        TranscriptionPhase.Failed => "失敗",
        _ => string.Empty,
    };

    public void Dispose() => ProgressNotifier.Changed -= OnProgressChanged;
}
