namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 將既有權限資料（RoleView.TabViewJson / MyUser.RoleViewId）
/// 回填至 RBAC 關聯表（Permission / RolePermissionMap / UserRole）。冪等。0.4.101 起不再從角色回填 UserTeam。
/// </summary>
public interface IRbacBackfillService
{
    Task RunAsync();
}
