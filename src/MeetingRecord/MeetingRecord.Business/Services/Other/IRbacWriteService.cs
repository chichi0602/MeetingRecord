namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 在角色/使用者編輯時，同步維護 RBAC 關聯表（雙寫），使新表與舊模型一致。
/// 所有方法皆為協調式（reconcile）：新增缺少的、移除多餘的。
/// </summary>
public interface IRbacWriteService
{
    /// <summary>依權限鍵集合同步某角色的 RolePermissionMap（缺少的 Permission 會自動建立）。</summary>
    Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys);

    /// <summary>同步某使用者的 UserRole。</summary>
    Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds);

    /// <summary>同步某使用者的 UserTeam。</summary>
    Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds);
}
