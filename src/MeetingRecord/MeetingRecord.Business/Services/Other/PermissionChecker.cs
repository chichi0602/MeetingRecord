using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

public sealed class PermissionChecker : IPermissionChecker
{
    private readonly BackendDBContext context;

    public PermissionChecker(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<bool> HasPermissionAsync(int userId, string permissionKey)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            return false;
        }

        if (user.IsAdmin)
        {
            return true;
        }

        var keys = await GetKeysForRolesAsync(await GetRoleIdsAsync(user.Id, user.RoleViewId));
        if (keys.Contains(permissionKey))
        {
            return true;
        }

        // 舊制相容：擁有裸頁面鍵者，視為具備該頁全部動作。
        var page = PermissionKey.PageOf(permissionKey);
        return page is not null && keys.Contains(page);
    }

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        return user is null
            ? []
            : await GetKeysForRolesAsync(await GetRoleIdsAsync(user.Id, user.RoleViewId));
    }

    /// <summary>使用者的角色來自 UserRole 關聯（多角色）；並容錯併入 legacy RoleViewId。</summary>
    private async Task<List<int>> GetRoleIdsAsync(int userId, int? legacyRoleViewId)
    {
        var roleIds = await context.UserRole
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Select(x => x.RoleViewId)
            .ToListAsync();

        if (legacyRoleViewId.HasValue && !roleIds.Contains(legacyRoleViewId.Value))
        {
            roleIds.Add(legacyRoleViewId.Value);
        }

        return roleIds;
    }

    private async Task<HashSet<string>> GetKeysForRolesAsync(List<int> roleIds)
    {
        if (roleIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var keys = await context.RolePermissionMap
            .AsNoTracking()
            .Where(m => roleIds.Contains(m.RoleViewId))
            .Join(context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .Distinct()
            .ToListAsync();

        return keys.ToHashSet(StringComparer.Ordinal);
    }
}
