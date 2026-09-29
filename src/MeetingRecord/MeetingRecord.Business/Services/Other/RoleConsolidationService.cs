using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 把角色收斂成「管理者／一般使用者」兩種（0.4.98）。每次啟動都跑，冪等。
///
/// <para>
/// 管理者靠 <c>MyUser.IsAdmin</c> 短路放行，不需要專屬角色；所以只維護一個角色「一般使用者」，
/// **所有帳號（含管理者）都只掛它**——<c>AuthenticationStateHelper.Check</c> 要求每個人都要有主要角色。
/// </para>
/// <para>
/// ⚠️ 權限用 <see cref="IRbacWriteService.SyncRolePermissionsAsync"/> 同步而不是回填：
/// 舊的預設角色擁有全部權限，<see cref="RbacBackfillService"/> 只會新增不會移除，收不回來。
/// 同步**只在升級當下做一次**（角色剛建立或剛改名），之後以角色管理頁為準。
/// 其他舊角色列留在資料庫但不再有人使用，刻意不刪（刪了稽核紀錄的角色名稱就對不回去）。
/// </para>
/// </summary>
public sealed class RoleConsolidationService
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IRbacWriteService rbacWriteService;
    private readonly ILogger<RoleConsolidationService> logger;

    public RoleConsolidationService(
        BackendDBContext context,
        RolePermissionService rolePermissionService,
        IRbacWriteService rbacWriteService,
        ILogger<RoleConsolidationService> logger)
    {
        this.context = context;
        this.rolePermissionService = rolePermissionService;
        this.rbacWriteService = rbacWriteService;
        this.logger = logger;
    }

    /// <summary>執行收斂，回傳「一般使用者」角色的 Id（啟動時的預設帳號要掛它）。</summary>
    public async Task<int> RunAsync()
    {
        var (role, isNewOrRenamed) = await EnsureGeneralRoleAsync();

        // 權限只在「剛建立或剛從舊預設角色改名」時設定一次。之後管理者可以在角色管理頁調整，
        // 每次啟動都覆寫的話，那一頁改了等於沒改（0.4.97 以前就是這樣才讓一般帳號拿到全部權限）。
        if (isNewOrRenamed)
        {
            var permissions = rolePermissionService.GetGeneralUserPermissionNames();

            // TabViewJson 已不是權限來源，但角色編輯畫面以它回填，保持一致免得看起來矛盾。
            role.TabViewJson = JsonSerializer.Serialize(permissions);
            await context.SaveChangesAsync();
            await rbacWriteService.SyncRolePermissionsAsync(role.Id, permissions);
        }

        var users = await context.MyUser.ToListAsync();
        foreach (var user in users.Where(x => x.RoleViewId != role.Id))
        {
            user.RoleViewId = role.Id;
        }
        await context.SaveChangesAsync();

        foreach (var user in users)
        {
            await rbacWriteService.SyncUserRolesAsync(user.Id, [role.Id]);
        }

        logger.LogInformation("Role consolidation completed. RoleViewId={RoleViewId}, Users={Users}", role.Id, users.Count);
        return role.Id;
    }

    private async Task<(RoleView Role, bool IsNewOrRenamed)> EnsureGeneralRoleAsync()
    {
        var role = await context.RoleView.FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色);
        if (role is not null)
        {
            return (role, false);
        }

        // 沿用舊預設角色那一列（改名）而不是另建：既有帳號的 RoleViewId、預設團隊都掛在它上面。
        role = await context.RoleView.FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.舊版預設角色);
        if (role is not null)
        {
            role.Name = MagicObjectHelper.預設角色;
            logger.LogInformation("Renamed legacy default role. RoleViewId={RoleViewId}", role.Id);
        }
        else
        {
            role = new RoleView { Name = MagicObjectHelper.預設角色 };
            context.RoleView.Add(role);
            logger.LogInformation("Seeded general user role.");
        }

        await context.SaveChangesAsync();
        return (role, true);
    }
}
