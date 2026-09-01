namespace MeetingRecord.Web.Components.Layout;

public sealed class SidebarMenuItemModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Url { get; set; }

    /// <summary>
    /// 側邊欄的區塊標題（例如「核心功能」「功能選單」），只有頂層節點需要標註。
    /// 由 <c>Menu.json</c> 宣告，未標註者歸入預設區塊。
    /// </summary>
    public string? Section { get; set; }

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
            // Section 必須一併複製：ApplyPermissionStructure 與 FilterAuthorizedMenuItems
            // 各會 Clone 一次，漏掉這行會讓分區資訊被清空，且不會有任何錯誤訊息。
            Section = Section,
            SubMenu = subMenu ?? SubMenu.Select(item => item.CloneWith()).ToList()
        };
    }
}
