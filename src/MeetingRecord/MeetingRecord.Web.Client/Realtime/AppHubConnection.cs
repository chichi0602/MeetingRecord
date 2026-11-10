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
/// </summary>
public sealed class AppHubConnection(NavigationManager navigation) : IAsyncDisposable
{
    public const string Path = "/hubs/app";
    public const string NotificationsChangedEvent = "NotificationsChanged";

    private HubConnection? connection;

    /// <summary>某些通知有變動（新通知或已讀）。</summary>
    public event Func<Task>? NotificationsChanged;

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
        connection.Reconnected += _ => RaiseAsync(Reconnected);

        try
        {
            await connection.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"推播通道連不上（{ex.Message}），畫面不會即時更新。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private static Task RaiseAsync(Func<Task>? handler)
        => handler is null ? Task.CompletedTask : Task.WhenAll(handler.GetInvocationList().Cast<Func<Task>>().Select(h => h()));
}
