using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Realtime;

/// <summary>
/// WASM 頁的進度面板資料來源（1.1.15）：打 <c>/api/v1/jobs</c>；伺服器推播「工作有變動」或推播通道重連後重查
/// （斷線期間的推播可能漏掉）。查不到（例如暫時連不上）就當成沒有工作，不讓面板掛掉。
/// </summary>
public sealed class ApiJobProgressSource(JobApi api, AppHubConnection hub, PermissionView permissionView) : IJobProgressSource
{
    public async Task<IReadOnlyList<BackgroundJobDto>> GetJobsAsync()
        => (await api.GetAsync()).Data ?? [];

    public async Task<bool> CanCancelAsync()
    {
        await permissionView.LoadAsync();
        return permissionView.Can(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit);
    }

    public Task CancelAsync(BackgroundJobDto job) => api.CancelAsync(job.Kind, job.MeetingId);

    public Task DismissAsync(BackgroundJobDto job) => api.DismissAsync(job.Kind, job.MeetingId);

    public Task DismissAllAsync() => api.DismissAllAsync();

    public IDisposable Subscribe(Func<Task> onChanged)
    {
        hub.JobsChanged += onChanged;
        hub.Reconnected += onChanged;
        _ = hub.StartAsync();
        return new Subscription(() =>
        {
            hub.JobsChanged -= onChanged;
            hub.Reconnected -= onChanged;
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
