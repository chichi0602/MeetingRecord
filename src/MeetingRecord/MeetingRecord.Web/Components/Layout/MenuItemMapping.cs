using MeetingRecord.Dtos.Sessions;

namespace MeetingRecord.Web.Components.Layout;

/// <summary>
/// 伺服器端選單模型轉成共用的 <see cref="MenuItemDto"/>（1.1.5）。Session 選單 API 與 Server 頁的 MainLayout 共用，
/// 兩種頁面畫的選單才會一模一樣（WASM-01 端對端情境比對兩邊的連結）。
/// </summary>
public static class MenuItemMapping
{
    public static MenuItemDto ToDto(SidebarMenuItemModel item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Icon = item.Icon,
        Url = item.Url,
        Section = item.Section,
        SubMenu = item.SubMenu.Select(ToDto).ToList(),
    };

    public static List<MenuItemDto> ToDtos(IEnumerable<SidebarMenuItemModel> items) => items.Select(ToDto).ToList();
}
