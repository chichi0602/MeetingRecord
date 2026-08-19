using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
namespace MeetingRecord.Web.Components.Layout;

public partial class SidebarMenuNode : ComponentBase
{
    [Parameter, EditorRequired]
    public SidebarMenuItemModel Item { get; set; } = default!;

    [Parameter]
    public int Level { get; set; }

    [Parameter, EditorRequired]
    public string ItemKey { get; set; } = default!;

    private string GetMenuKey()
    {
        return ItemKey;
    }

    private NavLinkMatch GetMatchMode()
    {
        return string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;
    }

    private string GetMaterialIconKind()
    {
        var icon = Item.Icon?.Trim();

        if (string.IsNullOrWhiteSpace(icon))
        {
            return Item.HasChildren ? "folder_open" : "article";
        }

        return icon switch
        {
            "home" => "home",
            "dashboard" => "space_dashboard",
            "admin" => "admin_panel_settings",
            "users" => "group",
            "roles" => "security",
            "shield_person" => "security",
            "setting" => "settings",
            "ProjectFilled" => "workspaces",
            "CarryOutFilled" => "checklist",
            "EnterOutlined" => "event",
            _ => icon
        };
    }
}
