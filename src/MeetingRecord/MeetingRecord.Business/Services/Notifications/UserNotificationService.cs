using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Notifications;

/// <summary>
/// 站內通知的讀取與已讀（1.0.44），右上角鈴鐺用。
///
/// <para>
/// ⚠️ 每個方法都帶 <c>userId</c>、而且只動那個人的通知：鈴鐺是被背景事件喚醒的，
/// 要自己開短命的 scope 查（拿頁面的 DbContext 會撞上並行操作，見 <c>BackgroundJobProgressPanel</c>），
/// 那個 scope 裡沒有目前使用者，所以由鈴鐺傳入自己的 Id。
/// </para>
/// </summary>
public class UserNotificationService
{
    private readonly BackendDBContext context;
    private readonly IUserNotificationNotifier notifier;

    public UserNotificationService(BackendDBContext context, IUserNotificationNotifier notifier)
    {
        this.context = context;
        this.notifier = notifier;
    }

    public Task<int> GetUnreadCountAsync(int userId)
        => context.UserNotification.AsNoTracking()
            .CountAsync(x => x.UserId == userId && x.ReadAt == null);

    /// <summary>最近的通知，新到舊（未讀、已讀都列）。</summary>
    public Task<List<UserNotification>> GetRecentAsync(int userId, int take = 20)
        => context.UserNotification.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync();

    /// <summary>標一則已讀；不是自己的通知不動。</summary>
    public async Task MarkReadAsync(int userId, int notificationId)
    {
        var changed = await context.UserNotification
            .Where(x => x.Id == notificationId && x.UserId == userId && x.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, DateTime.Now));

        if (changed > 0)
        {
            // 同一個人開了好幾個分頁，其他分頁的紅點也要跟著少。
            notifier.Notify([userId]);
        }
    }

    public async Task MarkAllReadAsync(int userId)
    {
        var changed = await context.UserNotification
            .Where(x => x.UserId == userId && x.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, DateTime.Now));

        if (changed > 0)
        {
            notifier.Notify([userId]);
        }
    }

    /// <summary>清掉某個時間點以前的通知（保留期限背景工作用）。</summary>
    public Task<int> PurgeBeforeAsync(DateTime before)
        => context.UserNotification.Where(x => x.CreatedAt < before).ExecuteDeleteAsync();
}
