using MeetingRecord.Dtos.Jobs;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 背景工作進度面板的資料來源（1.1.15 起，比照通知鈴的 <see cref="INotificationSource"/>）。
/// 實作是 <c>ApiJobProgressSource</c>：打 <c>api/v1/jobs</c> 並聽推播。
/// </summary>
public interface IJobProgressSource
{
    /// <summary>目前使用者看得到的工作（已依資料範圍過濾、進行中的排前面）。</summary>
    Task<IReadOnlyList<BackgroundJobDto>> GetJobsAsync();

    /// <summary>能不能取消工作（會議紀錄的修改權限）；只決定畫面上要不要顯示「取消」，真正的檢查在伺服器。</summary>
    Task<bool> CanCancelAsync();

    Task CancelAsync(BackgroundJobDto job);

    /// <summary>關閉通知（工作照跑）。</summary>
    Task DismissAsync(BackgroundJobDto job);

    Task DismissAllAsync();

    /// <summary>進度有變動時呼叫 <paramref name="onChanged"/>（可能在背景執行緒）；回傳值 Dispose 時取消訂閱。</summary>
    IDisposable Subscribe(Func<Task> onChanged);
}
