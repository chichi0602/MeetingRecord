namespace MeetingRecord.Models.Admins;

public class RolePermission
{
    public List<RolePermissionGroup> Groups { get; set; } = new();

    public RolePermission Clone()
    {
        return new RolePermission
        {
            Groups = Groups.Select(group => group.Clone()).ToList()
        };
    }
}

public class RolePermissionGroup
{
    public string Name { get; set; } = string.Empty;
    public bool Enable { get; set; } = false;
    public List<RolePermissionNode> Permissions { get; set; } = new();

    public RolePermissionGroup Clone()
    {
        return new RolePermissionGroup
        {
            Name = Name,
            Enable = Enable,
            Permissions = Permissions.Select(permission => permission.Clone()).ToList()
        };
    }
}

public class RolePermissionNode
{
    public string Name { get; set; } = string.Empty;
    /// <summary>裸頁面鍵＝該頁全部動作（舊制相容；勾選後動作明細停用）。</summary>
    public bool Enable { get; set; } = false;
    /// <summary>動作明細：動作代碼（view/create/edit/delete/export）→ 是否勾選。</summary>
    public Dictionary<string, bool> Actions { get; set; } = new();

    public RolePermissionNode Clone()
    {
        return new RolePermissionNode
        {
            Name = Name,
            Enable = Enable,
            Actions = new Dictionary<string, bool>(Actions)
        };
    }
}
