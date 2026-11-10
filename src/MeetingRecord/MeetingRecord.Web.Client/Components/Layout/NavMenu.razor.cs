using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Sessions;

namespace MeetingRecord.Web.Components.Layout;

/// <summary>
/// 側邊選單（1.1.5 起放在 WASM 專案，Blazor Server 頁與 WASM 頁共用同一份畫面）。
/// 選單與系統名稱由外框（<c>AppShell</c>）傳入：Server 頁來自 <c>SidebarMenuService</c>，WASM 頁來自 <c>GET /api/v1/session/menu</c>，
/// 兩邊用同一套權限過濾規則。以前這裡自己再載一次選單（與 MainLayout 重複）。
/// </summary>
public partial class NavMenu : ComponentBase, IDisposable
{
    [Parameter]
    public bool IsSidebarCollapsed { get; set; }

    /// <summary>已依權限過濾好的選單。</summary>
    [Parameter]
    public IReadOnlyList<MenuItemDto> MenuItems { get; set; } = [];

    /// <summary>側邊欄顯示的系統名稱。以設定檔為單一來源，不寫死在標記裡。</summary>
    [Parameter]
    public string SystemName { get; set; } = string.Empty;

    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private IReadOnlyList<MenuItemDto>? loadedMenuItems;
    private IReadOnlyList<SidebarMenuSection> Sections { get; set; } = [];
    private string? ActiveMenuPath { get; set; }
    private string[] OpenKeys { get; set; } = [];
    private string[] SelectedKeys { get; set; } = [];
    private bool previousSidebarCollapsed;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(loadedMenuItems, MenuItems))
        {
            loadedMenuItems = MenuItems;
            Sections = SidebarMenuSectionBuilder.Build(MenuItems);
            UpdateActiveMenuPath();
            SyncMenuStateFromRoute();
        }

        if (previousSidebarCollapsed == IsSidebarCollapsed)
        {
            return;
        }

        previousSidebarCollapsed = IsSidebarCollapsed;
        SyncOpenKeysForSidebarState();
    }

    private void UpdateActiveMenuPath()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        ActiveMenuPath = TryFindActiveMenuPath(MenuItems, normalizedCurrentPath, "root", out var activePath)
            ? activePath
            : null;

        Logger.LogDebug("Updated active menu path. Path={Path}, ActiveMenuPath={ActiveMenuPath}", normalizedCurrentPath, ActiveMenuPath);
    }

    private void SyncMenuStateFromRoute()
    {
        SelectedKeys = string.IsNullOrWhiteSpace(ActiveMenuPath)
            ? []
            : [ActiveMenuPath];

        SyncOpenKeysForSidebarState();
    }

    private void SyncOpenKeysForSidebarState()
    {
        OpenKeys = IsSidebarCollapsed
            ? []
            : GetAncestorKeys(ActiveMenuPath);
    }

    private static string[] GetAncestorKeys(string? activeMenuPath)
    {
        if (string.IsNullOrWhiteSpace(activeMenuPath))
        {
            return [];
        }

        var segments = activeMenuPath.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 2)
        {
            return [];
        }

        var ancestors = new List<string>(segments.Length - 2);
        for (var index = 2; index < segments.Length; index++)
        {
            ancestors.Add(string.Join('-', segments[..index]));
        }

        return [.. ancestors];
    }

    private string GetCollapsedItemClass(string itemKey)
    {
        var className = "collapsed-menu-item";

        if (IsMenuKeyActive(itemKey))
        {
            className += " collapsed-menu-item-active";
        }

        return className;
    }

    private string GetCollapsedFlyoutItemClass(string itemKey)
    {
        var className = "collapsed-flyout-item";

        if (string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal))
        {
            className += " collapsed-flyout-item-active";
        }

        return className;
    }

    /// <summary>收合態 flyout 裡的群組列（第二層有子項者）；目前頁面在它底下時一併標示選中。</summary>
    private string GetCollapsedFlyoutGroupClass(string itemKey)
    {
        var className = "collapsed-flyout-item collapsed-flyout-group-trigger";

        if (IsMenuKeyActive(itemKey))
        {
            className += " collapsed-flyout-item-active";
        }

        return className;
    }

    private bool IsMenuKeyActive(string itemKey)
    {
        return string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal)
            || (ActiveMenuPath?.StartsWith($"{itemKey}-", StringComparison.Ordinal) ?? false);
    }

    private static string GetMaterialIconKind(MenuItemDto item)
    {
        var icon = item.Icon?.Trim();

        if (string.IsNullOrWhiteSpace(icon))
        {
            return item.HasChildren ? "folder_open" : "article";
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

    private static bool TryFindActiveMenuPath(IReadOnlyList<MenuItemDto> items, string currentPath, string parentKey, out string? activePath)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var currentKey = $"{parentKey}-{index}";

            if (!string.IsNullOrWhiteSpace(item.Url) && IsMatchingUrl(item.Url, currentPath))
            {
                activePath = currentKey;
                return true;
            }

            if (item.HasChildren && TryFindActiveMenuPath(item.SubMenu, currentPath, currentKey, out activePath))
            {
                return true;
            }
        }

        activePath = null;
        return false;
    }

    private static bool IsMatchingUrl(string url, string currentPath)
    {
        var normalizedTargetPath = url.Trim();
        if (string.IsNullOrEmpty(normalizedTargetPath))
        {
            return false;
        }

        normalizedTargetPath = normalizedTargetPath.StartsWith('/') ? normalizedTargetPath : $"/{normalizedTargetPath}";
        if (string.Equals(normalizedTargetPath, "/", StringComparison.Ordinal))
        {
            return string.Equals(currentPath, "/", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(currentPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith($"{normalizedTargetPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        Logger.LogDebug("Navigation menu location changed. Uri={Uri}", e.Location);
        UpdateActiveMenuPath();
        SyncMenuStateFromRoute();
        InvokeAsync(StateHasChanged);
    }

    private void HandleOpenKeysChanged(string[] openKeys)
    {
        OpenKeys = openKeys;
    }

    public void Dispose()
    {
        Logger.LogDebug("Disposing navigation menu.");
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
