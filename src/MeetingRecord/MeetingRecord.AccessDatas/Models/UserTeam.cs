namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 使用者與團隊的多對多關聯（取代團隊綁在角色 RoleView.DefaultTeamsJson，改為直接綁使用者）。
/// </summary>
public class UserTeam
{
    public int Id { get; set; }
    public int MyUserId { get; set; }
    public MyUser? MyUser { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
}
