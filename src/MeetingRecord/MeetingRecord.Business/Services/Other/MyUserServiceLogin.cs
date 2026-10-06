using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Business.Services.Other;

public class MyUserServiceLogin
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IAuditLogService auditLogService;

    private const int MaxFailedAccessAttempts = 5;
    private const int LockoutMinutes = 15;

    public IMapper Mapper { get; }
    public IConfiguration Configuration { get; }
    public ILogger<MyUserServiceLogin> Logger { get; }

    public MyUserServiceLogin(
        BackendDBContext context,
        IMapper mapper,
        IConfiguration configuration,
        ILogger<MyUserServiceLogin> logger,
        RolePermissionService rolePermissionService,
        IAuditLogService auditLogService)
    {
        this.context = context;
        Mapper = mapper;
        Configuration = configuration;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
        this.auditLogService = auditLogService;
    }

    /// <summary>
    /// API 換發權杖前重查帳號（0.4.115）。以前只看換發權杖本身的內容，不查資料庫：
    /// 被停用、鎖定或刪除的人，在換發權杖有效的 7 天內都能一直換到新的存取權杖，
    /// 管理者身分也照舊權杖重簽。回傳 null 表示不能換發。
    /// </summary>
    public async Task<MyUser?> GetUserForTokenRefreshAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null
            || !user.Status
            || user.MustChangePassword
            || (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > DateTime.UtcNow))
        {
            Logger.LogWarning("Token refresh rejected by account state. UserId={UserId}", userId);
            return null;
        }

        return user;
    }

    public async Task<(string, MyUser?)> LoginAsync(string username, string password)
    {
        Logger.LogInformation("Login attempt started for Account={Account}.", username);

        try
        {
            MyUser? item = await context.MyUser
                .FirstOrDefaultAsync(x => x.Account == username);

            if (item is null)
            {
                Logger.LogWarning("Login failed because account was not found. Account={Account}", username);
                await auditLogService.WriteAsync("Login.Failed", success: false, actorAccount: username, detail: "帳號不存在");
                return ("帳號或者密碼不正確", null);
            }

            if (item.LockoutEndUtc.HasValue && item.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                Logger.LogWarning("Login blocked because account is locked. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", username, item.Id, item.LockoutEndUtc);
                await auditLogService.WriteAsync("Login.LockedOut", success: false, actorUserId: item.Id, actorAccount: username);
                return ("帳號已鎖定，請稍後再試。", null);
            }

            PasswordVerificationOutcome outcome = SecurePasswordHasher.VerifyPassword(password, item.Password, item.Salt);
            if (outcome == PasswordVerificationOutcome.Failed)
            {
                item.AccessFailedCount++;
                if (item.AccessFailedCount >= MaxFailedAccessAttempts)
                {
                    item.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                    Logger.LogWarning("Account locked after too many failed attempts. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", username, item.Id, item.LockoutEndUtc);
                }
                else
                {
                    Logger.LogWarning("Login failed because password validation failed. Account={Account}, UserId={UserId}, AccessFailedCount={AccessFailedCount}", username, item.Id, item.AccessFailedCount);
                }

                await context.SaveChangesAsync();
                string failAction = item.LockoutEndUtc is not null ? "Login.LockedOut" : "Login.Failed";
                await auditLogService.WriteAsync(failAction, success: false, actorUserId: item.Id, actorAccount: username, detail: $"AccessFailedCount={item.AccessFailedCount}");
                return ("帳號或者密碼不正確", null);
            }

            // 停用的帳號在這裡就擋下（0.4.113）。以前登入成功後才由 AuthenticationStateHelper 發現停用而登出，
            // 畫面會閃進系統一秒又跳回登入頁，看起來像當機。放在密碼驗證成功之後：密碼錯誤時一律回同一句話，
            // 不讓人藉此試探哪些帳號存在或已停用。正確密碼不算失敗次數，也不動既有的失敗計數。
            if (!item.Status)
            {
                Logger.LogWarning("Login blocked because account is disabled. Account={Account}, UserId={UserId}", username, item.Id);
                await auditLogService.WriteAsync("Login.Disabled", success: false, actorUserId: item.Id, actorAccount: username);
                return ("此帳號已停用，請聯絡管理者。", null);
            }

            bool changed = false;
            if (outcome == PasswordVerificationOutcome.SuccessRehashNeeded)
            {
                item.Password = SecurePasswordHasher.HashPassword(password);
                changed = true;
                Logger.LogInformation("Password hash upgraded to PBKDF2 for UserId={UserId}.", item.Id);
            }

            if (item.AccessFailedCount != 0 || item.LockoutEndUtc is not null)
            {
                item.AccessFailedCount = 0;
                item.LockoutEndUtc = null;
                changed = true;
            }

            if (changed)
            {
                await context.SaveChangesAsync();
            }

            await auditLogService.WriteAsync("Login.Success", success: true, actorUserId: item.Id, actorAccount: item.Account);
            Logger.LogInformation("Login validation succeeded for Account={Account}, UserId={UserId}.", username, item.Id);
            return (string.Empty, item);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login attempt failed unexpectedly for Account={Account}.", username);
            throw;
        }
    }
}
