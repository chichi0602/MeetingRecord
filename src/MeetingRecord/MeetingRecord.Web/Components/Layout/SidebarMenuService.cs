using System.Text.Json;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Caching;

namespace MeetingRecord.Web.Components.Layout;

public sealed class SidebarMenuService
{
    private const string MenuCacheKey = "sidebar:menu:raw";

    /// <summary>
    /// 選單項目 Id → 權限鍵的宣告式對應（取代原本的「位置索引」耦合，重排選單不會錯位）。
    /// 新增受控頁面時，於此加入 Id→權限鍵即可。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> MenuPermissionMap = new Dictionary<int, string>
    {
        [1] = MagicObjectHelper.角色_首頁,
        [2] = MagicObjectHelper.角色_專案管理,
        [21] = MagicObjectHelper.角色_專案項目,
        [3] = MagicObjectHelper.角色_系統管理,
        [31] = MagicObjectHelper.角色_使用者管理,
        [32] = MagicObjectHelper.角色_角色管理,
        [5] = MagicObjectHelper.角色_資料定義,
        [51] = MagicObjectHelper.角色_分類清單,
        [52] = MagicObjectHelper.角色_團隊清單,
        [4] = MagicObjectHelper.角色_登出,
    };

    private readonly IWebHostEnvironment environment;
    private readonly ILogger<SidebarMenuService> logger;
    private readonly ICacheService cacheService;

    public SidebarMenuService(
        IWebHostEnvironment environment,
        ILogger<SidebarMenuService> logger,
        ICacheService cacheService)
    {
        this.environment = environment;
        this.logger = logger;
        this.cacheService = cacheService;
    }

    public async Task<IReadOnlyList<SidebarMenuItemModel>> LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper authenticationStateHelper)
    {
        var items = await LoadMenuItemsAsync();
        var permissionMappedItems = ApplyPermissionStructure(items);
        var authorizedItems = FilterAuthorizedMenuItems(permissionMappedItems, authenticationStateHelper);

        logger.LogInformation("Loaded authorized sidebar menu successfully. ItemCount={ItemCount}", authorizedItems.Count);
        return authorizedItems;
    }

    private async Task<IReadOnlyList<SidebarMenuItemModel>> LoadMenuItemsAsync()
        => await cacheService.GetOrCreateAsync<List<SidebarMenuItemModel>>(
            MenuCacheKey,
            () => Task.FromResult(ReadMenuItemsFromDisk().ToList()));

    private IReadOnlyList<SidebarMenuItemModel> ReadMenuItemsFromDisk()
    {
        var menuFilePath = Path.Combine(environment.ContentRootPath, MagicObjectHelper.Menu結構定義);
        if (!File.Exists(menuFilePath))
        {
            logger.LogWarning("Sidebar menu file not found: {MenuFilePath}", menuFilePath);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(menuFilePath);
            return JsonSerializer.Deserialize<List<SidebarMenuItemModel>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load sidebar menu from {MenuFilePath}", menuFilePath);
            return [];
        }
    }

    private IReadOnlyList<SidebarMenuItemModel> ApplyPermissionStructure(IReadOnlyList<SidebarMenuItemModel> items)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            var permissionName = MenuPermissionMap.TryGetValue(item.Id, out var mapped) ? mapped : item.Name;
            var subMenu = item.HasChildren
                ? ApplyPermissionStructure(item.SubMenu).ToList()
                : new List<SidebarMenuItemModel>();

            result.Add(item.CloneWith(subMenu, permissionName));
        }

        return result;
    }

    private List<SidebarMenuItemModel> FilterAuthorizedMenuItems(
        IReadOnlyList<SidebarMenuItemModel> items,
        AuthenticationStateHelper authenticationStateHelper)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            var filteredChildren = item.HasChildren
                ? FilterAuthorizedMenuItems(item.SubMenu, authenticationStateHelper)
                : [];

            var permissionNames = GetPermissionNames(item);
            var hasPermission = permissionNames.Count == 0
                || permissionNames.Any(authenticationStateHelper.CheckAccessPage);

            if (!hasPermission && filteredChildren.Count == 0)
            {
                continue;
            }

            result.Add(item.CloneWith(filteredChildren));
        }

        return result;
    }

    private static List<string> GetPermissionNames(SidebarMenuItemModel item)
    {
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            result.Add(item.Name);
        }

        if (!string.IsNullOrWhiteSpace(item.PermissionName)
            && result.Contains(item.PermissionName, StringComparer.Ordinal) == false)
        {
            result.Add(item.PermissionName);
        }

        return result;
    }
}
