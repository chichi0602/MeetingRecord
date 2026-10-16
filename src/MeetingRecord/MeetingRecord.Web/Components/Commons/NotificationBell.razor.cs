using Microsoft.AspNetCore.Components;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Notifications;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右上角的站內通知鈴鐺（1.0.44）。
///
/// <para>
/// 使用者問「有人改了期限，一起負責那項專案的人要怎麼知道」：待辦被改時 <c>TodoChangeRecorder</c>
/// 寫通知給專案成員，並透過 <see cref="IUserNotificationNotifier"/> 廣播；這裡收到名單含自己才重新查。
/// </para>
///
/// <para>
/// ⚠️ 查詢一律自開短命的 scope（比照 <c>BackgroundJobProgressPanel</c>）：事件是別人存檔時觸發的，
/// 拿頁面那個 DbContext 查會和頁面正在跑的查詢撞在一起。
/// </para>
/// </summary>
public partial class NotificationBell : ComponentBase, IDisposable
{
    private const int RecentCount = 20;

    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = default!;

    [Inject]
    private IUserNotificationNotifier Notifier { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<NotificationBell> Logger { get; set; } = default!;

    /// <summary>目前使用者；登入檢查完成前是 0，那時不查、也不顯示。</summary>
    [Parameter]
    public int UserId { get; set; }

    private int loadedUserId;
    private int unreadCount;
    private List<UserNotification> items = [];
    private bool isOpen;
    private bool disposed;

    protected override void OnInitialized() => Notifier.Changed += OnNotified;

    protected override async Task OnParametersSetAsync()
    {
        if (UserId > 0 && UserId != loadedUserId)
        {
            loadedUserId = UserId;
            await RefreshAsync();
        }
    }

    private void OnNotified(IReadOnlyCollection<int> userIds)
    {
        if (loadedUserId <= 0 || !userIds.Contains(loadedUserId))
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            if (disposed)
            {
                return;
            }

            await RefreshAsync();
            StateHasChanged();
        });
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<UserNotificationService>();
            unreadCount = await service.GetUnreadCountAsync(loadedUserId);

            // 下拉沒打開時只要紅點數字，清單等打開再查。
            if (isOpen)
            {
                items = await service.GetRecentAsync(loadedUserId, RecentCount);
            }
        }
        catch (Exception ex)
        {
            // 鈴鐺查不到不該讓整頁壞掉：記 log、維持上一次的狀態。
            Logger.LogWarning(ex, "Loading notifications failed. UserId={UserId}", loadedUserId);
        }
    }

    private async Task ToggleAsync()
    {
        isOpen = !isOpen;
        if (isOpen)
        {
            await RefreshAsync();
        }
    }

    private void Close() => isOpen = false;

    private async Task OpenAsync(UserNotification item)
    {
        if (item.ReadAt is null)
        {
            await WithServiceAsync(service => service.MarkReadAsync(loadedUserId, item.Id));
        }

        isOpen = false;
        if (item.TodoId is { } todoId)
        {
            NavigationManager.NavigateTo($"/todos?todoId={todoId}");
        }
    }

    private async Task MarkAllReadAsync()
    {
        await WithServiceAsync(service => service.MarkAllReadAsync(loadedUserId));
        await RefreshAsync();
    }

    private async Task WithServiceAsync(Func<UserNotificationService, Task> action)
    {
        try
        {
            using var scope = ScopeFactory.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<UserNotificationService>());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Updating notifications failed. UserId={UserId}", loadedUserId);
        }
    }

    /// <summary>「剛剛」「5 分鐘前」「3 小時前」，超過一天顯示日期時間。</summary>
    private static string DescribeTime(DateTime createdAt)
    {
        var elapsed = DateTime.Now - createdAt;
        return elapsed.TotalMinutes < 1 ? "剛剛"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分鐘前"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} 小時前"
            : createdAt.ToString("MM/dd HH:mm");
    }

    public void Dispose()
    {
        disposed = true;
        Notifier.Changed -= OnNotified;
        GC.SuppressFinalize(this);
    }
}
