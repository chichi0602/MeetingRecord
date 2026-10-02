using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.DataAccess;

public class MyUserService
{
    private readonly BackendDBContext context;
    private readonly IRbacWriteService rbacWriteService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public IMapper Mapper { get; }
    public ILogger<MyUserService> Logger { get; }

    public MyUserService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MyUserService> logger,
        IRbacWriteService rbacWriteService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
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

    /// <summary>彙整使用者指派摘要（帳號、角色 Id、團隊）供稽核 Detail。</summary>
    private static string BuildAssignmentDetail(string account, MyUserAdapterModel paraObject)
    {
        var roleIds = new List<int>();
        if (paraObject.RoleViewId.HasValue)
        {
            roleIds.Add(paraObject.RoleViewId.Value);
        }
        if (paraObject.AdditionalRoleIds is not null)
        {
            roleIds.AddRange(paraObject.AdditionalRoleIds);
        }
        var teams = (paraObject.TeamNames ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return $"account={account}; roleIds=[{string.Join(",", roleIds.Distinct())}]; teams=[{string.Join(",", teams)}]";
    }

    public async Task<DataRequestResult<MyUserAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading users. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MyUserAdapterModel> result = new();
        IQueryable<MyUser> dataSource = context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Account.Contains(dataRequest.Search) ||
                x.Name.Contains(dataRequest.Search) ||
                (x.Email ?? string.Empty).Contains(dataRequest.Search) ||
                (x.RoleView != null && x.RoleView.Name.Contains(dataRequest.Search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MyUserAdapterModel.Account))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Account).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Account).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Email))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Email).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Email).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.RoleViewName))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.StatusText))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.RoleText))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsAdmin).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsAdmin).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.CreateAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreateAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.UpdateAt))
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

        List<MyUser> records = await dataSource.ToListAsync();
        List<MyUserAdapterModel> adapterModelObjects = Mapper.Map<List<MyUserAdapterModel>>(records);
        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OtherDependencyData(adapterModelItem);
        }

        // 清單要顯示每個人的所有角色與團隊（0.4.101）：一次撈這一頁的，不要逐列查。
        var userIds = adapterModelObjects.Select(x => x.Id).ToList();
        var roleNames = (await context.UserRole.AsNoTracking()
                .Where(x => userIds.Contains(x.MyUserId))
                .Join(context.RoleView, ur => ur.RoleViewId, r => r.Id, (ur, r) => new { ur.MyUserId, r.Name })
                .ToListAsync())
            .ToLookup(x => x.MyUserId, x => x.Name);
        var teamNames = (await context.UserTeam.AsNoTracking()
                .Where(x => userIds.Contains(x.MyUserId))
                .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => new { ut.MyUserId, t.Name })
                .ToListAsync())
            .ToLookup(x => x.MyUserId, x => x.Name);
        foreach (var item in adapterModelObjects)
        {
            item.RoleNames = [.. roleNames[item.Id].Order()];
            item.TeamNames = [.. teamNames[item.Id].Order()];
        }

        result.Result = adapterModelObjects;
        Logger.LogDebug("Loaded users successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MyUserAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading user by id. Id={UserId}", id);

        MyUser? item = await context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("User not found. Id={UserId}", id);
            return new MyUserAdapterModel();
        }

        MyUserAdapterModel result = Mapper.Map<MyUserAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }


    public async Task<VerifyRecordResult> AddAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogInformation("Creating user. Account={Account}, RoleViewId={RoleViewId}", paraObject.Account, paraObject.RoleViewId);

        try
        {
            if (string.IsNullOrWhiteSpace(paraObject.Password))
            {
                Logger.LogWarning("User creation rejected because password is empty. Account={Account}", paraObject.Account);
                return VerifyRecordResultFactory.Build(false, "新增使用者時必須輸入密碼。");
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser itemParameter = Mapper.Map<MyUser>(paraObject);
            itemParameter.RoleView = null;
            itemParameter.Salt = Guid.NewGuid().ToString();
            itemParameter.Password = SecurePasswordHasher.HashPassword(paraObject.Password);
            // 密碼是管理者給的，第一次登入要改成自己的（0.4.113）。
            itemParameter.MustChangePassword = !IsSupportAccount(itemParameter.Account);

            await context.MyUser.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            paraObject.Id = itemParameter.Id;
            await SyncAssignmentsAsync(itemParameter.Id, paraObject);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "User.Create", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: itemParameter.Id.ToString(),
                detail: BuildAssignmentDetail(itemParameter.Account, paraObject));

            Logger.LogInformation("User created successfully. UserId={UserId}, Account={Account}", itemParameter.Id, itemParameter.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create user. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "新增使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogInformation("Updating user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? currentItem = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("User update rejected because record was not found. UserId={UserId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的使用者資料。");
            }

            MyUser itemData = Mapper.Map<MyUser>(paraObject);
            itemData.RoleView = null;

            // 畫面模型沒有這個欄位，整筆蓋回時要先帶回原值，否則會被清成 false。
            itemData.MustChangePassword = currentItem.MustChangePassword;

            if (string.IsNullOrWhiteSpace(paraObject.Password))
            {
                itemData.Password = currentItem.Password;
                itemData.Salt = currentItem.Salt;
            }
            else
            {
                itemData.Salt = string.IsNullOrWhiteSpace(currentItem.Salt) ? Guid.NewGuid().ToString() : currentItem.Salt;
                itemData.Password = SecurePasswordHasher.HashPassword(paraObject.Password);

                // 管理者替別人重設密碼：對方下次登入要改（0.4.113）。改自己的不設；
                // support 被禁止改密碼，設了會卡在改密碼頁出不去。
                if (currentUserService.CurrentUser.Id != currentItem.Id && !IsSupportAccount(currentItem.Account))
                {
                    itemData.MustChangePassword = true;
                }
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            await SyncAssignmentsAsync(itemData.Id, paraObject);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "User.Update", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: itemData.Id.ToString(),
                detail: BuildAssignmentDetail(itemData.Account, paraObject));

            Logger.LogInformation("User updated successfully. UserId={UserId}, Account={Account}", itemData.Id, itemData.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "修改使用者失敗。", ex);
        }
    }

    /// <summary>雙寫：同步使用者的角色（主要 + 額外，多角色）與團隊（UserTeam）。</summary>
    private async Task SyncAssignmentsAsync(int userId, MyUserAdapterModel paraObject)
    {
        var roleIds = new List<int>();
        if (paraObject.RoleViewId.HasValue)
        {
            roleIds.Add(paraObject.RoleViewId.Value);
        }
        if (paraObject.AdditionalRoleIds is not null)
        {
            roleIds.AddRange(paraObject.AdditionalRoleIds);
        }
        await rbacWriteService.SyncUserRolesAsync(userId, roleIds.Distinct());

        var teamNames = (paraObject.TeamNames ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .ToList();
        var teamIds = await context.Team
            .AsNoTracking()
            .Where(t => teamNames.Contains(t.Name))
            .Select(t => t.Id)
            .ToListAsync();
        await rbacWriteService.SyncUserTeamsAsync(userId, teamIds);
    }

    /// <summary>載入使用者現有的額外角色（主要角色以外）與團隊名稱，供編輯畫面回填。</summary>
    public async Task<(List<int> AdditionalRoleIds, List<string> TeamNames)> GetUserAssignmentsAsync(int userId)
    {
        var primaryRoleId = await context.MyUser
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.RoleViewId)
            .FirstOrDefaultAsync();

        var allRoleIds = await context.UserRole
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Select(x => x.RoleViewId)
            .ToListAsync();

        var additional = allRoleIds
            .Where(id => !primaryRoleId.HasValue || id != primaryRoleId.Value)
            .Distinct()
            .ToList();

        var teamNames = await context.UserTeam
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => t.Name)
            .ToListAsync();

        return (additional, teamNames);
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting user. UserId={UserId}", id);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? item = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("User deletion rejected because record was not found. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的使用者資料。");
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                "User.Delete", success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: id.ToString(),
                detail: $"account={item.Account}");

            Logger.LogInformation("User deleted successfully. UserId={UserId}, Account={Account}", id, item.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete user. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for Account={Account}", paraObject.Account);

        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because account already exists. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        CleanTrackingHelper.Clean<MyUser>(context);
        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because record was not found. UserId={UserId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的使用者資料不存在。");
        }

        searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because account already exists. Account={Account}, UserId={UserId}", paraObject.Account, paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<VerifyRecordResult> ChangeOwnPasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        string confirmPassword)
    {
        Logger.LogInformation("Changing own password. UserId={UserId}", userId);

        MyUser? user = await context.MyUser
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            Logger.LogWarning("Own password change rejected because user was not found. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "找不到使用者資料。");
        }

        if (string.Equals(user.Account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("Own password change rejected for support account. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "系統預設開發帳號 support 禁止變更密碼。");
        }

        if (SecurePasswordHasher.VerifyPassword(currentPassword, user.Password, user.Salt) == PasswordVerificationOutcome.Failed)
        {
            Logger.LogWarning("Own password change rejected because current password is invalid. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "目前密碼不正確。");
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            Logger.LogWarning("Own password change rejected because new password is empty. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "新密碼不可為空白。");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            Logger.LogWarning("Own password change rejected because confirmation does not match. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "新密碼與確認密碼不一致。");
        }

        user.Salt = string.IsNullOrWhiteSpace(user.Salt) ? Guid.NewGuid().ToString() : user.Salt;
        user.Password = SecurePasswordHasher.HashPassword(newPassword);
        user.MustChangePassword = false;
        user.UpdateAt = DateTime.Now;

        await context.SaveChangesAsync();

        Logger.LogInformation("Own password changed successfully. UserId={UserId}", userId);
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>
    /// 查詢使用者目前是否已設定本地密碼（Google 帳號在設定 API 密碼前為 false）。
    /// </summary>
    public async Task<bool> HasLocalPasswordAsync(int userId)
    {
        CleanTrackingHelper.Clean<MyUser>(context);
        MyUser? user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        return user is not null && !string.IsNullOrEmpty(user.Password);
    }

    /// <summary>
    /// 由使用者自行設定 / 變更 API 密碼。
    /// 尚未設定本地密碼者（例如 Google 帳號）免驗舊密碼；已設定者需驗證目前密碼。
    /// </summary>
    public async Task<VerifyRecordResult> SetOwnApiPasswordAsync(
        int userId,
        string? currentPassword,
        string newPassword,
        string confirmPassword)
    {
        Logger.LogInformation("Setting own API password. UserId={UserId}", userId);

        MyUser? user = await context.MyUser
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            Logger.LogWarning("Set API password rejected because user was not found. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "找不到使用者資料。");
        }

        if (string.Equals(user.Account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("Set API password rejected for support account. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "系統預設開發帳號 support 禁止於此變更密碼。");
        }

        bool hasLocalPassword = !string.IsNullOrEmpty(user.Password);
        if (hasLocalPassword)
        {
            if (SecurePasswordHasher.VerifyPassword(currentPassword ?? string.Empty, user.Password, user.Salt) == PasswordVerificationOutcome.Failed)
            {
                Logger.LogWarning("Set API password rejected because current password is invalid. UserId={UserId}", userId);
                return VerifyRecordResultFactory.Build(false, "目前密碼不正確。");
            }
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return VerifyRecordResultFactory.Build(false, "新密碼不可為空白。");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return VerifyRecordResultFactory.Build(false, "新密碼與確認密碼不一致。");
        }

        user.Salt = string.IsNullOrWhiteSpace(user.Salt) ? Guid.NewGuid().ToString() : user.Salt;
        user.Password = SecurePasswordHasher.HashPassword(newPassword);
        user.MustChangePassword = false;
        user.UpdateAt = DateTime.Now;

        await context.SaveChangesAsync();

        Logger.LogInformation("Own API password set successfully. UserId={UserId}", userId);
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<List<RoleViewAdapterModel>> GetRoleViewsAsync()
    {
        Logger.LogDebug("Loading role views for user maintenance.");

        List<RoleView> roleViews = await context.RoleView
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();

        Logger.LogDebug("Loaded role views successfully. Count={Count}", roleViews.Count);
        return Mapper.Map<List<RoleViewAdapterModel>>(roleViews);
    }

    /// <summary>
    /// 清單上點狀態膠囊直接切換帳號狀態（0.4.105），不必進編輯視窗。
    /// </summary>
    public async Task<VerifyRecordResult> SetStatusAsync(int id, bool value)
    {
        Logger.LogInformation("Setting MyUser state. Id={Id}, Value={Value}", id, value);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);

            // 刻意不加 AsNoTracking：這裡要靠變更追蹤把欄位寫回去。
            MyUser? item = await context.MyUser.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                Logger.LogWarning("MyUser state update rejected because record was not found. Id={Id}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要更新的使用者資料。");
            }

            item.Status = value;
            item.UpdateAt = DateTime.Now;

            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            Logger.LogInformation("MyUser state updated. Id={Id}, Value={Value}", id, value);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set MyUser state. Id={Id}", id);
            return VerifyRecordResultFactory.Build(false, "更新。", ex);
        }
    }

    /// <summary>
    /// 批次把多位使用者加進一個團隊（0.4.101）。已經在裡面的略過，回傳實際新增的人數。
    /// 調整一批人的可見範圍靠這裡，不是靠改角色——角色只管能做什麼。
    /// </summary>
    public async Task<int> AddUsersToTeamAsync(IEnumerable<int> userIds, int teamId)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0 || !await context.Team.AnyAsync(x => x.Id == teamId))
        {
            return 0;
        }

        var existing = await context.UserTeam
            .Where(x => x.TeamId == teamId && ids.Contains(x.MyUserId))
            .Select(x => x.MyUserId)
            .ToListAsync();
        var validIds = await context.MyUser.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        var toAdd = validIds.Except(existing).ToList();

        context.UserTeam.AddRange(toAdd.Select(userId => new UserTeam { MyUserId = userId, TeamId = teamId }));
        await context.SaveChangesAsync();
        CleanTrackingHelper.Clean<UserTeam>(context);

        Logger.LogInformation("Users added to data group. TeamId={TeamId}, Added={Added}", teamId, toAdd.Count);
        return toAdd.Count;
    }

    /// <summary>批次把多位使用者移出一個團隊，回傳實際移除的人數。</summary>
    public async Task<int> RemoveUsersFromTeamAsync(IEnumerable<int> userIds, int teamId)
    {
        var ids = userIds.Distinct().ToList();
        var rows = await context.UserTeam
            .Where(x => x.TeamId == teamId && ids.Contains(x.MyUserId))
            .ToListAsync();

        context.UserTeam.RemoveRange(rows);
        await context.SaveChangesAsync();
        CleanTrackingHelper.Clean<UserTeam>(context);

        Logger.LogInformation("Users removed from data group. TeamId={TeamId}, Removed={Removed}", teamId, rows.Count);
        return rows.Count;
    }

    private Task OtherDependencyData(MyUserAdapterModel data)
    {
        data.Password = string.Empty;
        if (data.RoleView is not null)
        {
            data.RoleViewId = data.RoleView.Id;
        }

        return Task.CompletedTask;
    }

    private static bool IsSupportAccount(string account)
        => string.Equals(account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> NeedChangePasswordAsync(MyUserAdapterModel myUser)
    {
        Logger.LogDebug("Checking whether user must change password. UserId={UserId}", myUser.Id);

        CleanTrackingHelper.Clean<MyUser>(context);
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == myUser.Id);

        if (user == null)
        {
            Logger.LogWarning("Cannot check password-change requirement because user was not found. UserId={UserId}", myUser.Id);
            return false;
        }

        // 管理者建立或重設過密碼（0.4.113），或密碼仍是系統預設值，都要先改。
        bool result = user.MustChangePassword
            || SecurePasswordHasher.VerifyPassword(MagicObjectHelper.NeedChangePassword, user.Password, user.Salt)
                != PasswordVerificationOutcome.Failed;

        Logger.LogDebug("Password-change requirement check completed. UserId={UserId}, NeedChangePassword={NeedChangePassword}", myUser.Id, result);
        return result;
    }

    public async Task<VerifyRecordResult> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        Logger.LogInformation("Changing password for user. UserId={UserId}", userId);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? user = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == userId);

            if (user == null)
            {
                Logger.LogWarning("Change password rejected because user was not found. UserId={UserId}", userId);
                return VerifyRecordResultFactory.Build(false, "找不到使用者資料。");
            }

            if (SecurePasswordHasher.VerifyPassword(currentPassword, user.Password, user.Salt) == PasswordVerificationOutcome.Failed)
            {
                Logger.LogWarning("Change password rejected because current password is incorrect. UserId={UserId}", userId);
                return VerifyRecordResultFactory.Build(false, "目前密碼輸入錯誤。");
            }

            var newHash = SecurePasswordHasher.HashPassword(newPassword);

            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? trackedUser = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
            trackedUser!.Password = newHash;
            trackedUser.MustChangePassword = false;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            Logger.LogInformation("Password changed successfully. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change password. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "變更密碼失敗。", ex);
        }
    }
}
