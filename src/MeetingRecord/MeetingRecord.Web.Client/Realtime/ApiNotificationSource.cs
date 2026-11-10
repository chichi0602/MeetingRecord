using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Realtime;

/// <summary>
/// WASM 頁的通知鈴資料來源（1.1.6）：打 <c>/api/v1/notifications</c>；伺服器推播「通知有變動」或推播通道重連後通知鈴重查
/// （斷線期間的推播可能漏掉）。userId 參數用不到——API 一律以登入身分為準，只看得到自己的。
/// </summary>
public sealed class ApiNotificationSource(NotificationApi api, AppHubConnection hub) : INotificationSource
{
    public async Task<int> GetUnreadCountAsync(int userId)
        => (await api.GetUnreadCountAsync()).Data;

    public async Task<IReadOnlyList<NotificationDto>> GetRecentAsync(int userId, int take)
        => (await api.GetRecentAsync()).Data ?? [];

    public Task MarkReadAsync(int userId, int notificationId) => api.MarkReadAsync(notificationId);

    public Task MarkAllReadAsync(int userId) => api.MarkAllReadAsync();

    public IDisposable Subscribe(int userId, Func<Task> onChanged)
    {
        hub.NotificationsChanged += onChanged;
        hub.Reconnected += onChanged;
        _ = hub.StartAsync();
        return new Subscription(() =>
        {
            hub.NotificationsChanged -= onChanged;
            hub.Reconnected -= onChanged;
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
