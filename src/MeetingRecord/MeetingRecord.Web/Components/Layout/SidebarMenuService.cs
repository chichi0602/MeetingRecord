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
    public static readonly IReadOnlyDictionary<int, string> MenuPermissionMap = new Dictionary<int, string>
    {
        [12] = MagicObjectHelper.角色_使用說明,
        [21] = MagicObjectHelper.角色_專案項目,
        [22] = MagicObjectHelper.角色_待辦事項,
        [61] = MagicObjectHelper.角色_會議紀錄,
        [3] = MagicObjectHelper.角色_系統管理,
        [31] = MagicObjectHelper.角色_使用者管理,
        [32] = MagicObjectHelper.角色_角色管理,
        [5] = MagicObjectHelper.角色_資料定義,
        [51] = MagicObjectHelper.角色_分類清單,
        [52] = MagicObjectHelper.角色_團隊清單,
        [53] = MagicObjectHelper.角色_提示詞清單,
        [4] = MagicObjectHelper.角色_登出,
    };

    /// <summary>
    /// 登入即可看、不必設權限的選單項目 Id（0.4.97：儀表板）。
    /// ⚠️ 不能靠「不放進 <see cref="MenuPermissionMap"/>」達成——沒對應的項目會退回用 Name 當權限鍵，
    /// 角色沒有同名權限時照樣被藏起來。
    /// </summary>
    public static readonly IReadOnlySet<int> PublicMenuIds = new HashSet<int> { 11 };

    /// <summary>
    /// 只有管理者看得到的選單項目 Id（0.4.120：「監控與診斷」的系統健康監控 34、日誌等級設定 38、資料庫用量 30；0.4.121：日誌檢視 37；0.4.122：系統例外紀錄 39；
    /// 1.0.30：「AI 管理」的 Token 用量 33（原「AI 用量分析」，以前可勾給角色）、AI 對話紀錄 63）。
    /// 非管理者一律排除、**不看權限**——這些頁面不在權限目錄裡，但 0.4.119 以前「系統健康度」可以勾給角色，
    /// 舊資料庫可能還留著權限列；只靠權限鍵擋，殘留的列就會讓選單冒出來。頁面本身另以 CheckIsAdmin 守門。
    /// </summary>
    public static readonly IReadOnlySet<int> AdminOnlyMenuIds = new HashSet<int> { 34, 37, 38, 39, 30, 33, 63 };

    /// <summary>
    /// 純分組、本身沒有權限的選單節點 Id（0.4.120：「帳號與權限」35、「監控與診斷」36；1.0.30：「AI 管理」62）：
    /// 底下有任何一項看得到才顯示。⚠️ 不能讓它們退回「拿 Name 當權限鍵」那條路。
    /// </summary>
    public static readonly IReadOnlySet<int> GroupOnlyMenuIds = new HashSet<int> { 35, 36, 62 };

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
        var authorizedItems = FilterAuthorizedMenuItems(
            permissionMappedItems,
            authenticationStateHelper.CheckAccessPage,
            authenticationStateHelper.CheckIsAdmin());

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

    public static IReadOnlyList<SidebarMenuItemModel> ApplyPermissionStructure(IReadOnlyList<SidebarMenuItemModel> items)
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

    /// <summary>
    /// 依權限過濾選單（遞迴、任何深度）。純函式，供單元測試直接呼叫。
    /// </summary>
    /// <param name="canAccessPage">頁面權限判斷，正式環境是 <c>AuthenticationStateHelper.CheckAccessPage</c>（管理者恆真）。</param>
    /// <param name="isAdmin">是否為管理者；<see cref="AdminOnlyMenuIds"/> 只看這個值。</param>
    public static List<SidebarMenuItemModel> FilterAuthorizedMenuItems(
        IReadOnlyList<SidebarMenuItemModel> items,
        Func<string, bool> canAccessPage,
        bool isAdmin)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            if (AdminOnlyMenuIds.Contains(item.Id) && !isAdmin)
            {
                continue;
            }

            var filteredChildren = item.HasChildren
                ? FilterAuthorizedMenuItems(item.SubMenu, canAccessPage, isAdmin)
                : [];

            var permissionNames = GetPermissionNames(item);
            var hasPermission = !GroupOnlyMenuIds.Contains(item.Id)
                && (PublicMenuIds.Contains(item.Id)
                    || AdminOnlyMenuIds.Contains(item.Id)
                    || permissionNames.Count == 0
                    || permissionNames.Any(canAccessPage));

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
