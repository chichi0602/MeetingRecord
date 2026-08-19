namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 使用者與角色的多對多關聯（取代 MyUser.RoleViewId 單一外鍵，支援多角色）。
/// </summary>
public class UserRole
{
    public int Id { get; set; }
    public int MyUserId { get; set; }
    public MyUser? MyUser { get; set; }
    public int RoleViewId { get; set; }
    public RoleView? RoleView { get; set; }
}
