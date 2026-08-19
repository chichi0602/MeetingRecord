using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

public sealed class RbacWriteService : IRbacWriteService
{
    private readonly BackendDBContext context;

    public RbacWriteService(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task SyncRolePermissionsAsync(int roleViewId, IEnumerable<string> permissionKeys)
    {
        var desiredKeys = permissionKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var permissionIdByKey = await EnsurePermissionsAsync(desiredKeys);
        var desiredIds = desiredKeys.Select(k => permissionIdByKey[k]).ToHashSet();

        var existing = await context.RolePermissionMap
            .Where(x => x.RoleViewId == roleViewId)
            .ToListAsync();
        var existingIds = existing.Select(x => x.PermissionId).ToHashSet();

        foreach (var removed in existing.Where(x => !desiredIds.Contains(x.PermissionId)))
        {
            context.RolePermissionMap.Remove(removed);
        }

        foreach (var addId in desiredIds.Where(id => !existingIds.Contains(id)))
        {
            context.RolePermissionMap.Add(new RolePermissionMap { RoleViewId = roleViewId, PermissionId = addId });
        }

        await context.SaveChangesAsync();
    }

    public async Task SyncUserRolesAsync(int userId, IEnumerable<int> roleViewIds)
    {
        var desired = roleViewIds.ToHashSet();

        var existing = await context.UserRole.Where(x => x.MyUserId == userId).ToListAsync();
        var existingIds = existing.Select(x => x.RoleViewId).ToHashSet();

        foreach (var removed in existing.Where(x => !desired.Contains(x.RoleViewId)))
        {
            context.UserRole.Remove(removed);
        }

        foreach (var addId in desired.Where(id => !existingIds.Contains(id)))
        {
            context.UserRole.Add(new UserRole { MyUserId = userId, RoleViewId = addId });
        }

        await context.SaveChangesAsync();
    }

    public async Task SyncUserTeamsAsync(int userId, IEnumerable<int> teamIds)
    {
        var desired = teamIds.ToHashSet();

        var existing = await context.UserTeam.Where(x => x.MyUserId == userId).ToListAsync();
        var existingIds = existing.Select(x => x.TeamId).ToHashSet();

        foreach (var removed in existing.Where(x => !desired.Contains(x.TeamId)))
        {
            context.UserTeam.Remove(removed);
        }

        foreach (var addId in desired.Where(id => !existingIds.Contains(id)))
        {
            context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = addId });
        }

        await context.SaveChangesAsync();
    }

    private async Task<Dictionary<string, int>> EnsurePermissionsAsync(IReadOnlyCollection<string> keys)
    {
        var map = await context.Permission
            .Where(p => keys.Contains(p.Key))
            .ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal);

        var missing = keys.Where(k => !map.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            foreach (var key in missing)
            {
                context.Permission.Add(new Permission { Key = key, DisplayName = key });
            }

            await context.SaveChangesAsync();

            foreach (var permission in await context.Permission.Where(p => missing.Contains(p.Key)).ToListAsync())
            {
                map[permission.Key] = permission.Id;
            }
        }

        return map;
    }
}
