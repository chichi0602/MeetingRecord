using AntDesign;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Dtos.Jobs;
using Microsoft.JSInterop;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右下角常駐的背景工作進度面板（形式比照雲端硬碟的上傳進度）。
///
/// <para>
/// 掛在版面（<c>WasmLayout</c> 放進 <c>AppShell</c> 的常駐浮層），所以切到任何頁面都看得到。
/// 同時顯示語音轉錄與會議紀錄生成兩種工作——兩者都固定在右下角，各做一個面板會直接重疊，因此合併成同一個。
/// 資料來自 <see cref="IJobProgressSource"/>：可見性過濾、取消與關閉的規則都在伺服器（<c>BackgroundJobBoard</c>）。
/// </para>
/// </summary>
public partial class BackgroundJobProgressPanel : ComponentBase, IDisposable
{
    [Inject]
    public IJobProgressSource Source { get; set; } = default!;

    [Inject] private NotificationService Notifications { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private List<BackgroundJobDto> visibleJobs = [];

    /// <summary>
    /// 1.1.69 完成通知：上一次看到時還在跑的工作（Key）。這次變成完成或失敗、而且是自己的，就跳提示。
    /// null＝還沒載入過：第一次載入看到的「已完成」是之前就完成的，不提示。
    /// </summary>
    private HashSet<string>? runningBefore;
    private bool isCollapsed;
    private bool canCancel;
    private IDisposable? subscription;

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
        canCancel = await Source.CanCancelAsync();
        subscription = Source.Subscribe(OnProgressChangedAsync);
        await RefreshAsync();
    }

    /// <summary>可能由背景執行緒引發，必須切回 UI 執行緒才能碰元件狀態。</summary>
    private Task OnProgressChangedAsync()
        => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        });

    private async Task RefreshAsync()
    {
        visibleJobs = [.. await Source.GetJobsAsync()];
        await NotifyFinishedAsync();
    }

    /// <summary>
    /// 1.1.69：自己的工作從「進行中」變成完成或失敗時，右下角跳提示（點了直接打開那筆），
    /// 頁面在背景的話分頁標題加「(✓)」。只提示自己的，同團隊別人的工作不打擾。
    /// </summary>
    private async Task NotifyFinishedAsync()
    {
        var previous = runningBefore;
        runningBefore = visibleJobs.Where(x => x.IsRunning).Select(x => x.Key).ToHashSet();
        if (previous is null)
        {
            return;
        }

        var finished = visibleJobs
            .Where(x => x.IsMine && (x.IsCompleted || x.IsFailed) && previous.Contains(x.Key))
            .ToList();
        foreach (var job in finished)
        {
            var job1 = job;
            var config = new NotificationConfig
            {
                Message = job.IsFailed ? $"{job.KindText}失敗" : FinishedTitle(job),
                Description = job.IsFailed ? $"「{job.Title}」：{job.ErrorMessage}" : $"「{job.Title}」，點這裡打開。",
                NotificationType = job.IsFailed ? NotificationType.Error : NotificationType.Success,
                Placement = NotificationPlacement.BottomRight,
            };
            config.OnClick += () => Open(job1);
            _ = Notifications.Open(config);
        }

        if (finished.Count > 0)
        {
            try
            {
                await JsRuntime.InvokeVoidAsync("meetingRecordTabAlert.flag");
            }
            catch (JSException)
            {
                // 分頁標題只是加分，失敗不影響提示。
            }
        }
    }

    private static string FinishedTitle(BackgroundJobDto job) => job.NeedsSpeakerNames
        ? "轉錄完成，請標示講者"
        : job.Kind == BackgroundJobKinds.Transcription ? "轉錄完成" : "會議紀錄已產生";

    /// <summary>
    /// 1.1.69：打開這筆會議。有專案走專案頁的深連結（1.1.67），沒有就走會議頁；
    /// 等標示講者的未歸屬會議直接開標示視窗。
    /// </summary>
    private void Open(BackgroundJobDto job)
    {
        var url = job.ProjectId is { } projectId
            ? $"/projects?projectId={projectId}&meetingId={job.MeetingId}"
            : $"/meetings?meetingId={job.MeetingId}{(job.NeedsSpeakerNames ? "&speakers=1" : string.Empty)}";
        Navigation.NavigateTo(url);
    }

    private void ToggleCollapsed() => isCollapsed = !isCollapsed;

    /// <summary>
    /// 關閉鈕的說明。進行中時必須講清楚**工作會繼續跑**——
    /// 這顆按鈕在 0.4.55 之前只寫「關閉這一筆」，很容易被當成取消。
    /// </summary>
    private static string CloseButtonLabel(BackgroundJobDto job)
        => job.IsRunning ? "關閉通知（工作會繼續執行）" : "關閉這一筆";

    private static ProgressStatus ProgressStatusOf(BackgroundJobDto job) => job.IsCompleted
        ? ProgressStatus.Success
        : job.IsFailed
            ? ProgressStatus.Exception
            : ProgressStatus.Active;

    /// <summary>真正取消工作。排隊中的會在 worker 取出時被跳過，執行中的會直接中斷。</summary>
    private async Task CancelJob(BackgroundJobDto job)
    {
        if (!canCancel || !job.CanBeCancelled)
        {
            return;
        }

        await Source.CancelAsync(job);
    }

    private async Task Dismiss(BackgroundJobDto job)
    {
        await Source.DismissAsync(job);
        await RefreshAsync(); // 不必等推播繞一圈才消失。
    }

    private async Task DismissAll()
    {
        await Source.DismissAllAsync();
        await RefreshAsync();
    }

    public void Dispose() => subscription?.Dispose();
}
