using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>留版原因（<see cref="MeetingDraftVersion.Reason"/>）。</summary>
public static class MeetingDraftVersionReasons
{
    public const string Regenerate = "Regenerate";
    public const string Edit = "Edit";
    public const string Restore = "Restore";

    public static string ToText(string? reason) => reason switch
    {
        Regenerate => "重新產生前",
        Edit => "編修前",
        Restore => "還原前",
        _ => reason ?? string.Empty,
    };
}

/// <summary>
/// 會議紀錄留版（1.1.71）：<c>Meeting.DraftContent</c> 要被蓋掉之前呼叫，把目前的內容留一份。
///
/// <para>
/// ⚠️ 一律在改寫 <c>DraftContent</c>／<c>DraftPromptTemplateName</c>／<c>DraftAttendees</c> <b>之前</b>呼叫，
/// 舊版才帶得到它自己的提示詞與與會人員。只 Add 不存檔，和會議本身同一次 <c>SaveChanges</c>。
/// </para>
/// </summary>
public static class MeetingDraftVersionRecorder
{
    /// <summary>
    /// 內容是空的、或和最新一版完全相同（例如上次產生失敗、內容根本沒變）就不記；
    /// 記了之後超過 <see cref="MeetingDraftVersion.MaxPerMeeting"/> 份就刪最舊的。
    /// </summary>
    public static async Task<bool> SnapshotAsync(
        BackendDBContext context,
        Meeting meeting,
        string reason,
        int? userId,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meeting.DraftContent))
        {
            return false;
        }

        var existing = await context.MeetingDraftVersion
            .Where(x => x.MeetingId == meeting.Id)
            .OrderByDescending(x => x.SavedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0 && existing[0].Content == meeting.DraftContent)
        {
            return false;
        }

        context.MeetingDraftVersion.Add(new MeetingDraftVersion
        {
            MeetingId = meeting.Id,
            Content = meeting.DraftContent,
            PromptTemplateName = meeting.DraftPromptTemplateName,
            Attendees = meeting.DraftAttendees,
            Reason = reason,
            ContentAt = meeting.DraftCompletedAt ?? meeting.UpdatedAt,
            SavedByUserId = userId is > 0 ? userId : null,
            SavedByName = userName,
            SavedAt = DateTime.Now,
        });

        // 加上這一份之後只能有 MaxPerMeeting 份。
        context.MeetingDraftVersion.RemoveRange(existing.Skip(MeetingDraftVersion.MaxPerMeeting - 1));
        return true;
    }
}
