using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 確保「一般使用者」這個新帳號預設角色存在。每次啟動都跑，冪等。
///
/// <para>
/// 0.4.101 起角色只管「能做什麼」，可以多角色、權限取聯集，由管理者在角色管理與使用者管理自由設定。
/// 這裡**不動任何帳號的角色**（0.4.98～0.4.100 曾把所有人收斂成一般使用者，已拿掉），
/// 也不再每次啟動覆寫權限——0.4.97 以前就是那樣才讓一般帳號拿到全部權限。
/// </para>
/// <para>
/// 權限只在角色**剛建立或剛從舊名「預設角色」改名**時設成業務頁那一組，
/// 用 <see cref="IRbacWriteService.SyncRolePermissionsAsync"/> 同步（會移除多餘的權限）；之後以角色管理頁為準。
/// </para>
/// </summary>
public sealed class DefaultRoleSeeder
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IRbacWriteService rbacWriteService;
    private readonly ILogger<DefaultRoleSeeder> logger;

    public DefaultRoleSeeder(
        BackendDBContext context,
        RolePermissionService rolePermissionService,
        IRbacWriteService rbacWriteService,
        ILogger<DefaultRoleSeeder> logger)
    {
        this.context = context;
        this.rolePermissionService = rolePermissionService;
        this.rbacWriteService = rbacWriteService;
        this.logger = logger;
    }

    /// <summary>回傳「一般使用者」角色的 Id（啟動時的預設帳號沒有角色時掛它）。</summary>
    public async Task<int> RunAsync()
    {
        var (role, isNewOrRenamed) = await EnsureGeneralRoleAsync();

        if (isNewOrRenamed)
        {
            var permissions = rolePermissionService.GetGeneralUserPermissionNames();

            // TabViewJson 已不是權限來源，但角色編輯畫面以它回填，保持一致免得看起來矛盾。
            role.TabViewJson = JsonSerializer.Serialize(permissions);
            await context.SaveChangesAsync();
            await rbacWriteService.SyncRolePermissionsAsync(role.Id, permissions);
        }

        logger.LogInformation("Default role ensured. RoleViewId={RoleViewId}, Initialized={Initialized}", role.Id, isNewOrRenamed);
        return role.Id;
    }

    private async Task<(RoleView Role, bool IsNewOrRenamed)> EnsureGeneralRoleAsync()
    {
        var role = await context.RoleView.FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色);
        if (role is not null)
        {
            return (role, false);
        }

        // 沿用舊預設角色那一列（改名）而不是另建：既有帳號的 RoleViewId 都掛在它上面。
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
