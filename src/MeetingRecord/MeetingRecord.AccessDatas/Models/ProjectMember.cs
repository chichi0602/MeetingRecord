using MeetingRecord.Share.Enums;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 專案成員（0.4.99）。一般使用者只看得到自己是成員的專案，以及掛在那些專案底下的
/// 會議紀錄、待辦、附件與 AI 問答；管理者（<c>MyUser.IsAdmin</c>）不受限。
///
/// <para>
/// 取代 0.4.98 以前「會議靠 <c>Teams</c> 標籤控管、專案完全不控管」的做法。
/// 判斷一律經過 <c>ProjectAccessService</c>，不要在各服務自己查這張表。
/// </para>
/// </summary>
public class ProjectMember
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public Project? Project { get; set; }

    public int MyUserId { get; set; }

    public MyUser? MyUser { get; set; }

    public ProjectMemberRole Role { get; set; } = ProjectMemberRole.Collaborator;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
