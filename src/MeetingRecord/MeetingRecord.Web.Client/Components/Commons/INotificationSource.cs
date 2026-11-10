using MeetingRecord.Dtos.Notifications;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 通知鈴的資料來源（1.1.6，WASM 遷移）。鈴鐺本身只有一份（<see cref="NotificationBell"/>），兩種頁面各自提供資料：
/// Blazor Server 頁直接用伺服器服務與程序內的通知事件（<c>ServerNotificationSource</c>），
/// WASM 頁打 API、靠推播通道得知變動（<c>ApiNotificationSource</c>）。
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
