using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;

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
        await BackfillPermissionCatalogAsync();
        await BackfillRolePermissionsAsync();
        await BackfillUserRolesAsync();
        await BackfillUserTeamsAsync();
        logger.LogInformation("RBAC backfill completed.");
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

    private async Task BackfillUserTeamsAsync()
    {
        var teamIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in await context.Team.AsNoTracking().ToListAsync())
        {
            teamIdByName.TryAdd(team.Name, team.Id);
        }

        var rolesById = await context.RoleView.AsNoTracking().ToDictionaryAsync(x => x.Id);

        var existing = (await context.UserTeam
            .Select(x => new { x.MyUserId, x.TeamId })
            .ToListAsync())
            .Select(x => (x.MyUserId, x.TeamId))
            .ToHashSet();

        var users = await context.MyUser.AsNoTracking()
            .Where(x => x.RoleViewId != null)
            .ToListAsync();

        foreach (var user in users)
        {
            if (!rolesById.TryGetValue(user.RoleViewId!.Value, out var role))
            {
                continue;
            }

            foreach (var teamName in TeamJsonHelper.Deserialize(role.DefaultTeamsJson))
            {
                if (teamIdByName.TryGetValue(teamName, out var teamId)
                    && existing.Add((user.Id, teamId)))
                {
                    context.UserTeam.Add(new UserTeam
                    {
                        MyUserId = user.Id,
                        TeamId = teamId,
                    });
                }
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
