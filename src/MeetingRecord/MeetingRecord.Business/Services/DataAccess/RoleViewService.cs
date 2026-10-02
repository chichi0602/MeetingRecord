using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Admins;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using System.Text.Json;

namespace MeetingRecord.Business.Services.DataAccess;

public class RoleViewService
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IRbacWriteService rbacWriteService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public IMapper Mapper { get; }
    public ILogger<RoleViewService> Logger { get; }

    public RoleViewService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<RoleViewService> logger,
        RolePermissionService rolePermissionService,
        IRbacWriteService rbacWriteService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
        this.rbacWriteService = rbacWriteService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
    }

    /// <summary>取得目前操作者作為稽核 actor；未登入（Id==0）時回 null。</summary>
    private (int? ActorUserId, string? ActorAccount) ResolveActor()
    {
        var user = currentUserService.CurrentUser;
        return user.Id > 0 ? (user.Id, user.Account) : (null, null);
    }

    private static List<string> ParsePermissionKeys(string? tabViewJson)
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

    public async Task<DataRequestResult<RoleViewAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading role views. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<RoleViewAdapterModel> result = new();
        IQueryable<RoleView> dataSource = context.RoleView.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x => x.Name.Contains(dataRequest.Search));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(RoleViewAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.CreateAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreateAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.UpdateAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdateAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<RoleView> records = await dataSource.ToListAsync();
        List<RoleViewAdapterModel> adapterModelObjects = Mapper.Map<List<RoleViewAdapterModel>>(records);
        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OtherDependencyData(adapterModelItem);
        }

        result.Result = adapterModelObjects;
        Logger.LogDebug("Loaded role views successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<RoleViewAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading role view by id. RoleViewId={RoleViewId}", id);

        RoleView? item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Role view not found. RoleViewId={RoleViewId}", id);
            return new RoleViewAdapterModel();
        }

        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }

    public async Task<VerifyRecordResult> AddAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogInformation("Creating role view. Name={RoleName}", paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView itemParameter = Mapper.Map<RoleView>(paraObject);
            itemParameter.TabViewJson = rolePermissionService.GetPermissionInputToJson(paraObject.RolePermission);

            await context.RoleView.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<RoleView>(context);

            var permissionKeys = ParsePermissionKeys(itemParameter.TabViewJson);
            await rbacWriteService.SyncRolePermissionsAsync(itemParameter.Id, permissionKeys);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "Role.Create", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: itemParameter.Id.ToString(),
                detail: $"name={itemParameter.Name}; permissionKeyCount={permissionKeys.Count}");

            Logger.LogInformation("Role view created successfully. RoleViewId={RoleViewId}, Name={RoleName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create role view. Name={RoleName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增角色失敗。", ex);
        }
    }

    /// <summary>
    /// 一次建好 <see cref="RolePresets.All"/> 的預設角色（0.4.108），給剛上線、角色是空的系統用。
    /// 已有同名角色就略過、不覆蓋（可能已經調過權限），所以可以重複按。
    /// </summary>
    public async Task<VerifyRecordResult> AddPresetsAsync()
    {
        Logger.LogInformation("Applying role presets. PresetCount={PresetCount}", RolePresets.All.Count);

        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);

            // 在記憶體比對：與 BeforeAddCheckAsync 的名稱唯一語意一致，也不必在意 SQLite 的定序。
            var existing = new HashSet<string>(
                await context.RoleView.AsNoTracking().Select(x => x.Name).ToListAsync(),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            var (actorUserId, actorAccount) = ResolveActor();
            foreach (var preset in RolePresets.All.Where(x => !existing.Contains(x.Name.Trim())))
            {
                var role = new RoleView
                {
                    Name = preset.Name,
                    // TabViewJson 已不是權限來源，但角色編輯畫面以它回填勾選。
                    TabViewJson = JsonSerializer.Serialize(preset.PermissionKeys),
                };
                context.RoleView.Add(role);
                await context.SaveChangesAsync();
                CleanTrackingHelper.Clean<RoleView>(context);

                await rbacWriteService.SyncRolePermissionsAsync(role.Id, preset.PermissionKeys);
                await auditLogService.WriteAsync(
                    "Role.Create", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                    targetType: nameof(RoleView), targetId: role.Id.ToString(),
                    detail: $"name={role.Name}; preset=true; permissionKeyCount={preset.PermissionKeys.Count}");
                added++;
            }

            var skipped = RolePresets.All.Count - added;
            var message = skipped == 0
                ? $"已新增 {added} 個預設角色。"
                : $"已新增 {added} 個預設角色，略過 {skipped} 個（已有同名角色）。";

            Logger.LogInformation("Role presets applied. Added={Added}, Skipped={Skipped}", added, skipped);
            return VerifyRecordResultFactory.Build(true, message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to apply role presets.");
            return VerifyRecordResultFactory.Build(false, "建立預設角色失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogInformation("Updating role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);

        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView itemData = Mapper.Map<RoleView>(paraObject);
            itemData.TabViewJson = rolePermissionService.GetPermissionInputToJson(paraObject.RolePermission);

            RoleView? item = await context.RoleView
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Role view update rejected because record was not found. RoleViewId={RoleViewId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的角色資料。");
            }

            CleanTrackingHelper.Clean<RoleView>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<RoleView>(context);

            var permissionKeys = ParsePermissionKeys(itemData.TabViewJson);
            await rbacWriteService.SyncRolePermissionsAsync(itemData.Id, permissionKeys);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "Role.Update", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: itemData.Id.ToString(),
                detail: $"name={itemData.Name}; permissionKeyCount={permissionKeys.Count}");

            Logger.LogInformation("Role view updated successfully. RoleViewId={RoleViewId}, Name={RoleName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改角色失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting role view. RoleViewId={RoleViewId}", id);

        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView? item = await context.RoleView
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Role view deletion rejected because record was not found. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的角色資料。");
            }

            // 0.4.110：MyUser.RoleViewId 是 Restrict 外鍵，有人以這個角色當主要角色時資料庫會擋下刪除。
            // 先把那些人的主要角色換掉；任何人都不能變成沒有角色——沒有角色的帳號登入時會被登出，
            // 刪的若是管理者帳號正在用的角色，連管理者都進不了系統。
            var remainingRoleIds = await context.RoleView.AsNoTracking()
                .Where(x => x.Id != id)
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Name })
                .ToListAsync();
            if (remainingRoleIds.Count == 0)
            {
                Logger.LogWarning("Role view deletion rejected because it is the last role. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, "系統至少要保留一個角色，否則所有帳號都會無法登入。");
            }

            var fallbackRoleId = remainingRoleIds.FirstOrDefault(x => x.Name == MagicObjectHelper.預設角色)?.Id
                ?? remainingRoleIds[0].Id;

            await using var transaction = await context.Database.BeginTransactionAsync();
            CleanTrackingHelper.Clean<RoleView>(context);

            var affectedUsers = await context.MyUser.Where(x => x.RoleViewId == id).ToListAsync();
            foreach (var user in affectedUsers)
            {
                // 優先改用他身上還有的其他角色；沒有就改用一般使用者（或剩下的第一個角色）。
                var otherRoleId = await context.UserRole
                    .Where(x => x.MyUserId == user.Id && x.RoleViewId != id)
                    .OrderBy(x => x.RoleViewId)
                    .Select(x => (int?)x.RoleViewId)
                    .FirstOrDefaultAsync();
                user.RoleViewId = otherRoleId ?? fallbackRoleId;
                if (otherRoleId is null)
                {
                    context.UserRole.Add(new UserRole { MyUserId = user.Id, RoleViewId = fallbackRoleId });
                }
            }

            await context.SaveChangesAsync();
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            context.ChangeTracker.Clear();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "Role.Delete", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: id.ToString(),
                detail: $"name={item.Name}; reassignedPrimaryUsers={affectedUsers.Count}");

            Logger.LogInformation("Role view deleted successfully. RoleViewId={RoleViewId}, Name={RoleName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete role view. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除角色失敗。", ex);
        }
    }

    /// <summary>有多少人掛著這個角色（主要或額外），刪除前的確認視窗用。</summary>
    public async Task<int> CountUsersAsync(int roleViewId)
    {
        return await context.MyUser.AsNoTracking()
            .CountAsync(u => u.RoleViewId == roleViewId
                || context.UserRole.Any(r => r.MyUserId == u.Id && r.RoleViewId == roleViewId));
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for role view. Name={RoleName}", paraObject.Name);

        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == paraObject.Name);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because role name already exists. Name={RoleName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "角色名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);

        CleanTrackingHelper.Clean<RoleView>(context);
        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because role view was not found. RoleViewId={RoleViewId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的角色資料不存在。");
        }

        searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == paraObject.Name && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because role name already exists. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "角色名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private Task OtherDependencyData(RoleViewAdapterModel data)
    {
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();
        List<string> permissions;

        try
        {
            permissions = JsonSerializer.Deserialize<List<string>>(data.TabViewJson) ?? [];
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to deserialize role permissions. RoleViewId={RoleViewId}", data.Id);
            permissions = [];
        }

        rolePermissionService.SetPermissionInput(rolePermission, permissions);
        data.RolePermission = rolePermission;
        return Task.CompletedTask;
    }

    public async Task<RoleViewAdapterModel> Get預設新建帳號角色Async()
    {
        Logger.LogDebug("Loading default role view for new user creation.");

        RoleView? item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色);

        if (item is null)
        {
            Logger.LogWarning("Default role view was not found. RoleName={RoleName}", MagicObjectHelper.預設角色);
            return new RoleViewAdapterModel();
        }

        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }
}
