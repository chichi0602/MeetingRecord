using MeetingRecord.Dtos.Notifications;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 通知鈴（<see cref="NotificationBell"/>）的資料來源（1.1.6 起）。實作是 <c>ApiNotificationSource</c>：
/// 打 API、靠推播通道得知變動。
/// </summary>
public interface INotificationSource
{
    Task<int> GetUnreadCountAsync(int userId);

    Task<IReadOnlyList<NotificationDto>> GetRecentAsync(int userId, int take);

    /// <summary>標一則已讀；不是自己的通知不動。</summary>
    Task MarkReadAsync(int userId, int notificationId);

    Task MarkAllReadAsync(int userId);

    /// <summary>這個使用者的通知有變動時呼叫 <paramref name="onChanged"/>；Dispose 就取消。</summary>
    IDisposable Subscribe(int userId, Func<Task> onChanged);
}
