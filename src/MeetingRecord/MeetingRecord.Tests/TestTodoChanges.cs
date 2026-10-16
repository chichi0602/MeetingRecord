using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>測試用的待辦異動記錄器（1.0.44）：與服務共用同一個 context、同一個操作者。</summary>
internal static class TestTodoChanges
{
    public static TodoChangeRecorder CreateRecorder(
        BackendDBContext context,
        ProjectAccessService access,
        IAuditActorProvider actor,
        IUserNotificationNotifier? notifier = null)
        => new(context, access, actor, notifier ?? new RecordingNotificationNotifier());
}

/// <summary>記下每次「誰該重新查通知」，驗證鈴鐺會被叫醒。</summary>
internal sealed class RecordingNotificationNotifier : IUserNotificationNotifier
{
    public List<IReadOnlyCollection<int>> Calls { get; } = [];

    public event Action<IReadOnlyCollection<int>>? Changed;

    public void Notify(IReadOnlyCollection<int> userIds)
    {
        // 與正式的 UserNotificationNotifier 一致：沒有收件人就不廣播。
        if (userIds.Count == 0)
        {
            return;
        }

        Calls.Add(userIds);
        Changed?.Invoke(userIds);
    }
}
