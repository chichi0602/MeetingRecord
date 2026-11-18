using System.Collections.Concurrent;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 背景工作通知的「每人各自關閉」紀錄（1.1.68）。
///
/// <para>
/// 1.1.67 之前關閉通知是直接把通知器裡的項目刪掉——一個人按關閉，所有人的通知都不見，
/// 連進行中工作之後的完成／失敗也不會再出現。現在項目留著，只記下「誰關過這一趟」。
/// </para>
///
/// <para>
/// 同一筆會議重新排入（重新轉錄、重新產生）時要 <see cref="Reset"/>：那是新的一趟，之前關過的人要能再看到。
/// </para>
/// </summary>
public sealed class PerUserDismissals
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> dismissed = new();

    public void Dismiss(int meetingId, int userId)
        => dismissed.GetOrAdd(meetingId, _ => new ConcurrentDictionary<int, byte>())[userId] = 0;

    public bool IsDismissed(int meetingId, int userId)
        => dismissed.TryGetValue(meetingId, out var users) && users.ContainsKey(userId);

    /// <summary>這筆會議開始新的一趟，或項目已移除：清掉所有人的關閉紀錄。</summary>
    public void Reset(int meetingId) => dismissed.TryRemove(meetingId, out _);
}
