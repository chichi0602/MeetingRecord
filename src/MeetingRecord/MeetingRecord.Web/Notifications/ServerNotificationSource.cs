using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Dtos.Notifications;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Notifications;

/// <summary>
/// Blazor Server 頁的通知鈴資料來源（1.1.6，原本寫在 NotificationBell 裡）。
///
/// <para>
/// ⚠️ 查詢一律自開短命的 scope（比照 <c>BackgroundJobProgressPanel</c>）：事件是別人存檔時觸發的，
/// 拿頁面那個 DbContext 查會和頁面正在跑的查詢撞在一起。
/// </para>
/// </summary>
public sealed class ServerNotificationSource(IServiceScopeFactory scopeFactory, IUserNotificationNotifier notifier) : INotificationSource
{
    public async Task<int> GetUnreadCountAsync(int userId)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserNotificationService>().GetUnreadCountAsync(userId);
    }

    public async Task<IReadOnlyList<NotificationDto>> GetRecentAsync(int userId, int take)
    {
        using var scope = scopeFactory.CreateScope();
        var items = await scope.ServiceProvider.GetRequiredService<UserNotificationService>().GetRecentAsync(userId, take);
        return items.Select(x => new NotificationDto { Id = x.Id, Message = x.Message, CreatedAt = x.CreatedAt, ReadAt = x.ReadAt, TodoId = x.TodoId }).ToList();
    }

    public async Task MarkReadAsync(int userId, int notificationId)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UserNotificationService>().MarkReadAsync(userId, notificationId);
    }

    public async Task MarkAllReadAsync(int userId)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UserNotificationService>().MarkAllReadAsync(userId);
    }

    public IDisposable Subscribe(int userId, Func<Task> onChanged)
    {
        // 通知器只說「哪些人該重新查」；名單含自己才查，避免每則通知都讓所有連線打一次資料庫。
        void Handler(IReadOnlyCollection<int> userIds)
        {
            if (userIds.Contains(userId))
            {
                _ = onChanged();
            }
        }

        notifier.Changed += Handler;
        return new Subscription(() => notifier.Changed -= Handler);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
