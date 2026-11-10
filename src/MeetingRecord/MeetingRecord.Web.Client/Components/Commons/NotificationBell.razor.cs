using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Notifications;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 右上角的站內通知鈴鐺（1.0.44）。
///
/// <para>
/// 使用者問「有人改了期限，一起負責那項專案的人要怎麼知道」：待辦被改時 <c>TodoChangeRecorder</c>
/// 寫通知給專案成員並廣播「誰有新通知」；這裡收到自己的變動才重新查。
/// </para>
///
/// <para>
/// 1.1.6（WASM 遷移）起鈴鐺放在 WASM 專案、兩種頁面共用：資料經 <see cref="INotificationSource"/> 取得——
/// Server 頁直接用伺服器服務，WASM 頁打 API 並靠推播通道得知變動。
/// </para>
/// </summary>
public partial class NotificationBell : ComponentBase, IDisposable
{
    private const int RecentCount = 20;

    [Inject]
    private INotificationSource Source { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<NotificationBell> Logger { get; set; } = default!;

    /// <summary>目前使用者；登入檢查完成前是 0，那時不查、也不顯示。</summary>
    [Parameter]
    public int UserId { get; set; }

    private int loadedUserId;
    private int unreadCount;
    private IReadOnlyList<NotificationDto> items = [];
    private bool isOpen;
    private bool disposed;
    private IDisposable? subscription;

    protected override async Task OnParametersSetAsync()
    {
        if (UserId > 0 && UserId != loadedUserId)
        {
            loadedUserId = UserId;
            subscription?.Dispose();
            subscription = Source.Subscribe(UserId, OnChangedAsync);
            await RefreshAsync();
        }
    }

    private Task OnChangedAsync()
        => InvokeAsync(async () =>
        {
            if (disposed)
            {
                return;
            }

            await RefreshAsync();
            StateHasChanged();
        });

    private async Task RefreshAsync()
    {
        try
        {
            unreadCount = await Source.GetUnreadCountAsync(loadedUserId);

            // 下拉沒打開時只要紅點數字，清單等打開再查。
            if (isOpen)
            {
                items = await Source.GetRecentAsync(loadedUserId, RecentCount);
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

    private async Task OpenAsync(NotificationDto item)
    {
        if (item.ReadAt is null)
        {
            await WithSourceAsync(source => source.MarkReadAsync(loadedUserId, item.Id));
        }

        isOpen = false;
        if (item.TodoId is { } todoId)
        {
            NavigationManager.NavigateTo($"/todos?todoId={todoId}");
        }
    }

    private async Task MarkAllReadAsync()
    {
        await WithSourceAsync(source => source.MarkAllReadAsync(loadedUserId));
        await RefreshAsync();
    }

    private async Task WithSourceAsync(Func<INotificationSource, Task> action)
    {
        try
        {
            await action(Source);
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
        subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
