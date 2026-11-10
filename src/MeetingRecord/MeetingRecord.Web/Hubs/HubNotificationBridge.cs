using Microsoft.AspNetCore.SignalR;
using MeetingRecord.Business.Services.Notifications;

namespace MeetingRecord.Web.Hubs;

/// <summary>
/// 把程序內的通知事件轉送到 <see cref="AppHub"/>（1.1.2）：<see cref="IUserNotificationNotifier"/> 說「這些人有新通知」，
/// 這裡對那些人的群組送 <see cref="AppHub.NotificationsChanged"/>。Blazor Server 頁的通知鈴照舊直接訂閱事件，兩邊並存。
/// 送不出去只記警告：寫通知的那一方（例如改待辦）不能因為推播失敗而失敗。
/// </summary>
public sealed class HubNotificationBridge(
    IUserNotificationNotifier notifier,
    IHubContext<AppHub> hubContext,
    ILogger<HubNotificationBridge> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        notifier.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(IReadOnlyCollection<int> userIds)
        => _ = SendAsync(userIds);

    private async Task SendAsync(IReadOnlyCollection<int> userIds)
    {
        try
        {
            await hubContext.Clients.Groups(userIds.Select(AppHub.UserGroup).ToList()).SendAsync(AppHub.NotificationsChanged);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push notification change to hub. UserCount={UserCount}", userIds.Count);
        }
    }
}
