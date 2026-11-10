using AntDesign;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Dtos.Jobs;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右下角常駐的背景工作進度面板（形式比照雲端硬碟的上傳進度）。
///
/// <para>
/// 掛在版面（Server 頁的 <c>MainLayout</c>、1.1.15 起 WASM 頁的 <c>WasmLayout</c>），所以切到任何頁面都看得到。
/// 同時顯示語音轉錄與會議紀錄生成兩種工作——兩者都固定在右下角，各做一個面板會直接重疊，因此合併成同一個。
/// 資料來自 <see cref="IJobProgressSource"/>：可見性過濾、取消與關閉的規則都在伺服器（<c>BackgroundJobBoard</c>）。
/// </para>
/// </summary>
public partial class BackgroundJobProgressPanel : ComponentBase, IDisposable
{
    [Inject]
    public IJobProgressSource Source { get; set; } = default!;

    private List<BackgroundJobDto> visibleJobs = [];
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

    private async Task RefreshAsync() => visibleJobs = [.. await Source.GetJobsAsync()];

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
        await RefreshAsync(); // WASM 頁不必等推播繞一圈才消失。
    }

    private async Task DismissAll()
    {
        await Source.DismissAllAsync();
        await RefreshAsync();
    }

    public void Dispose() => subscription?.Dispose();
}
