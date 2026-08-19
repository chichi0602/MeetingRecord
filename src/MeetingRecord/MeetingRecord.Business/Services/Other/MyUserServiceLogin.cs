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
