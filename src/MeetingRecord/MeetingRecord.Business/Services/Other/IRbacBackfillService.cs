namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 將既有權限資料（RoleView.TabViewJson / MyUser.RoleViewId / RoleView.DefaultTeamsJson）
/// 回填至新的 RBAC 關聯表（Permission / RolePermissionMap / UserRole / UserTeam）。冪等。
/// </summary>
public interface IRbacBackfillService
{
    Task RunAsync();
}
