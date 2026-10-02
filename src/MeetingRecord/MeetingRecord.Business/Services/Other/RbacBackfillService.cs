using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

public sealed class RbacBackfillService : IRbacBackfillService
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly ILogger<RbacBackfillService> logger;

    public RbacBackfillService(
        BackendDBContext context,
        RolePermissionService rolePermissionService,
        ILogger<RbacBackfillService> logger)
    {
        this.context = context;
        this.rolePermissionService = rolePermissionService;
        this.logger = logger;
    }

    public async Task RunAsync()
    {
        await RenameLegacyPermissionKeysAsync();
        await BackfillPermissionCatalogAsync();
        await BackfillRolePermissionsAsync();
        await BackfillUserRolesAsync();
        logger.LogInformation("RBAC backfill completed.");
    }

    /// <summary>
    /// 0.4.101 曾把「團隊清單」改名為「資料群組」，0.4.102 改回「團隊清單」。就地改 <see cref="Permission"/> 的鍵
    /// （含「資料群組:edit」這類動作鍵）與角色的 TabViewJson，<see cref="RolePermissionMap"/> 掛的是 PermissionId，所以已勾的角色不會掉權限。
    /// 必須在 <see cref="BackfillPermissionCatalogAsync"/> 之前跑，否則會先長出一個空的新鍵、舊鍵就改不過去。
    /// </summary>
    private async Task RenameLegacyPermissionKeysAsync()
    {
        const string legacy = "資料群組";
        const string current = MagicObjectHelper.角色_團隊清單;

        var keys = await context.Permission.ToListAsync();
        var existingKeys = keys.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var permission in keys)
        {
            var renamed = RenameKey(permission.Key, legacy, current);
            if (renamed is null || existingKeys.Contains(renamed))
            {
                continue;
            }

            permission.Key = renamed;
            if (permission.DisplayName is not null)
            {
                permission.DisplayName = RenameKey(permission.DisplayName, legacy, current) ?? permission.DisplayName;
            }
        }

        foreach (var role in await context.RoleView.ToListAsync())
        {
            var names = DeserializePermissionNames(role.TabViewJson);
            if (names.Any(x => RenameKey(x, legacy, current) is not null))
            {
                role.TabViewJson = JsonSerializer.Serialize(names.Select(x => RenameKey(x, legacy, current) ?? x).ToList());
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>裸鍵或「舊鍵:動作」改成新鍵；不是舊鍵回傳 null。</summary>
    private static string? RenameKey(string key, string legacy, string current)
    {
        if (key == legacy)
        {
            return current;
        }

        return PermissionKey.PageOf(key) == legacy ? current + key[legacy.Length..] : null;
    }

    private async Task BackfillPermissionCatalogAsync()
    {
        var groups = rolePermissionService.GetRoleListPermissionAllName();
        var existingKeys = (await context.Permission.Select(x => x.Key).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        var sort = 0;
        foreach (var group in groups)
        {
            var groupName = group.FirstOrDefault() ?? string.Empty;
            foreach (var name in group)
            {
                sort++;
                if (existingKeys.Add(name))
                {
                    context.Permission.Add(new Permission
                    {
                        Key = name,
                        DisplayName = name,
                        GroupName = groupName,
                        SortOrder = sort,
                    });
                }
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task BackfillRolePermissionsAsync()
    {
        var permissionByKey = await context.Permission
            .ToDictionaryAsync(x => x.Key, x => x.Id, StringComparer.Ordinal);

        var existing = (await context.RolePermissionMap
            .Select(x => new { x.RoleViewId, x.PermissionId })
            .ToListAsync())
            .Select(x => (x.RoleViewId, x.PermissionId))
            .ToHashSet();

        var roles = await context.RoleView.AsNoTracking().ToListAsync();
        foreach (var role in roles)
        {
            foreach (var name in DeserializePermissionNames(role.TabViewJson))
            {
                if (permissionByKey.TryGetValue(name, out var permissionId)
                    && existing.Add((role.Id, permissionId)))
                {
                    context.RolePermissionMap.Add(new RolePermissionMap
                    {
                        RoleViewId = role.Id,
                        PermissionId = permissionId,
                    });
                }
            }
        }

        await context.SaveChangesAsync();
    }

    private async Task BackfillUserRolesAsync()
    {
        var existing = (await context.UserRole
            .Select(x => new { x.MyUserId, x.RoleViewId })
            .ToListAsync())
            .Select(x => (x.MyUserId, x.RoleViewId))
            .ToHashSet();

        var users = await context.MyUser.AsNoTracking()
            .Where(x => x.RoleViewId != null)
            .ToListAsync();

        foreach (var user in users)
        {
            var roleViewId = user.RoleViewId!.Value;
            if (existing.Add((user.Id, roleViewId)))
            {
                context.UserRole.Add(new UserRole
                {
                    MyUserId = user.Id,
                    RoleViewId = roleViewId,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    private static List<string> DeserializePermissionNames(string? tabViewJson)
    {
        if (string.IsNullOrWhiteSpace(tabViewJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tabViewJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
