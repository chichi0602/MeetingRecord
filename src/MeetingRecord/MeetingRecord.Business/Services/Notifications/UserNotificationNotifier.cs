namespace MeetingRecord.Business.Services.Notifications;

/// <summary>
/// 「某些人有新通知了」的即時廣播（1.0.44），讓右上角鈴鐺不必輪詢。
///
/// <para>
/// 形狀比照 <c>TranscriptionProgressNotifier</c>：Singleton＋事件。通知本身存在資料庫，
/// 這裡只負責說「哪些人該重新查一次」；鈴鐺只在名單含自己時才查，避免每則通知都讓所有連線打一次資料庫。
/// ⚠️ 事件在寫入的那條執行緒上觸發，訂閱者要用 <c>InvokeAsync</c> 切回自己的連線。
/// </para>
/// </summary>
public interface IUserNotificationNotifier
{
    event Action<IReadOnlyCollection<int>>? Changed;

    /// <summary>通知這些使用者重新查（新通知、或自己在別的分頁標了已讀）。</summary>
    void Notify(IReadOnlyCollection<int> userIds);
}

public sealed class UserNotificationNotifier : IUserNotificationNotifier
{
    public event Action<IReadOnlyCollection<int>>? Changed;

    public void Notify(IReadOnlyCollection<int> userIds)
    {
        if (userIds.Count > 0)
        {
            Changed?.Invoke(userIds);
        }
    }
}
