namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 角色與權限的多對多關聯（取代 RoleView.TabViewJson）。
/// 命名為 Map 以避免與 UI 綁定模型 MeetingRecord.Models.Admins.RolePermission 衝突。
/// </summary>
public class RolePermissionMap
{
    public int Id { get; set; }
    public int RoleViewId { get; set; }
    public RoleView? RoleView { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
}
