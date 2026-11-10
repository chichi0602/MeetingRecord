using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeetingRecord.Web.Client.Realtime;

/// <summary>
/// 連伺服器的推播通道 <c>/hubs/app</c>（1.1.2，WASM 遷移 M1）。同網域，瀏覽器自動帶登入 Cookie。
///
/// <para>
/// 推播不帶資料，只說「什麼變了」，訂閱者收到後自己打 API 重查；斷線會自動重連，重連後也要重查（斷線期間可能漏掉推播）。
/// 連不上不讓頁面掛掉：資料仍可以靠 API 取得，只是不會即時更新。
/// </para>
///
/// <para>
/// ⚠️ <c>WithAutomaticReconnect</c> 只處理「連上之後才斷」，而且試 4 次（約 42 秒）就放棄。第一次就連不上（剛好斷網、伺服器重啟中），
/// 或斷太久放棄了，以前整個分頁的即時更新就再也不會恢復；1.1.23 起改由 <see cref="RetryStartAsync"/> 在背景持續重試，連上後照樣發出 <see cref="Reconnected"/>。
/// </para>
/// </summary>
public sealed class AppHubConnection(NavigationManager navigation) : IAsyncDisposable
{
    public const string Path = "/hubs/app";
    public const string NotificationsChangedEvent = "NotificationsChanged";
    public const string JobsChangedEvent = "JobsChanged";

    /// <summary>背景重試的間隔：從 2 秒起每次加倍，最多 30 秒。</summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private HubConnection? connection;
    private bool retrying;
    private bool disposed;

    /// <summary>某些通知有變動（新通知或已讀）。</summary>
    public event Func<Task>? NotificationsChanged;

    /// <summary>背景工作（上傳、轉錄、會議紀錄生成）的進度有變動（1.1.15）。</summary>
    public event Func<Task>? JobsChanged;

    /// <summary>斷線後重新連上；斷線期間的推播可能漏掉，要重查。</summary>
    public event Func<Task>? Reconnected;

    public HubConnectionState State => connection?.State ?? HubConnectionState.Disconnected;

    public async Task StartAsync()
    {
        if (connection is not null)
        {
            return;
        }

        connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(Path))
            .WithAutomaticReconnect()
            .Build();
        connection.On(NotificationsChangedEvent, () => RaiseAsync(NotificationsChanged));
        connection.On(JobsChangedEvent, () => RaiseAsync(JobsChanged));
        connection.Reconnected += _ => RaiseAsync(Reconnected);

        // 自動重連放棄後會走到這裡（使用者主動停止只有 Dispose，已由 disposed 擋掉）。
        connection.Closed += error =>
        {
            _ = RetryStartAsync();
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"推播通道連不上（{ex.Message}），稍後自動重試；在那之前畫面不會即時更新。");
            _ = RetryStartAsync();
        }
    }

    /// <summary>在背景一直試到連上為止；連上後發出 <see cref="Reconnected"/>，讓訂閱者補查斷線期間漏掉的變動。</summary>
    private async Task RetryStartAsync()
    {
        if (retrying || disposed || connection is null)
        {
            return;
        }

        retrying = true;
        try
        {
            var delay = FirstRetryDelay;
            while (!disposed && connection.State == HubConnectionState.Disconnected)
            {
                await Task.Delay(delay);
                try
                {
                    await connection.StartAsync();
                    await RaiseAsync(Reconnected);
                    return;
                }
                catch (Exception)
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                }
            }
        }
        finally
        {
            retrying = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private static Task RaiseAsync(Func<Task>? handler)
        => handler is null ? Task.CompletedTask : Task.WhenAll(handler.GetInvocationList().Cast<Func<Task>>().Select(h => h()));
}
