namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 使用者與團隊（Team）的多對多關聯。團隊＝「誰的資料」：這是使用者看得到哪些專案的唯一來源，角色不帶團隊。
/// </summary>
public class UserTeam
{
    public int Id { get; set; }
    public int MyUserId { get; set; }
    public MyUser? MyUser { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
}
