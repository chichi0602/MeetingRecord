namespace MeetingRecord.Web.Components.Layout;

public sealed class SidebarMenuItemModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Url { get; set; }
    public List<SidebarMenuItemModel> SubMenu { get; set; } = [];

    public bool HasChildren => SubMenu.Count > 0;

    public SidebarMenuItemModel CloneWith(List<SidebarMenuItemModel>? subMenu = null, string? permissionName = null)
    {
        return new SidebarMenuItemModel
        {
            Id = Id,
            Name = Name,
            PermissionName = permissionName ?? PermissionName,
            Icon = Icon,
            Url = Url,
            SubMenu = subMenu ?? SubMenu.Select(item => item.CloneWith()).ToList()
        };
    }
}
