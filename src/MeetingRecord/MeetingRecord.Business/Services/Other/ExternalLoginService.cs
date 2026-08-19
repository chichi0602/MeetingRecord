using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 處理第三方（Google）登入時的帳號查找、連結與自動建立。
/// </summary>
public class ExternalLoginService
{
    private readonly BackendDBContext context;
    private readonly ILogger<ExternalLoginService> logger;

    public ExternalLoginService(BackendDBContext context, ILogger<ExternalLoginService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    /// <summary>
    /// 依 Google 身分查找或建立本地使用者。
    /// 1) 先以 GoogleId 比對；2) 否則以 Email 連結既有帳號；3) 否則自動建立停用中的新帳號。
    /// </summary>
    public async Task<MyUser> FindOrCreateAsync(
        string provider,
        string googleSubject,
        string email,
        string displayName,
        string defaultRoleName)
    {
        logger.LogInformation(
            "External login resolving user. Provider={Provider}, Subject={Subject}, Email={Email}.",
            provider, googleSubject, email);

        // 1) 以 GoogleId 比對既有連結
        MyUser? user = await context.MyUser
            .FirstOrDefaultAsync(x => x.GoogleId == googleSubject);
        if (user is not null)
        {
            logger.LogInformation("External login matched existing GoogleId. UserId={UserId}.", user.Id);
            return user;
        }

        // 2) 以 Email 連結既有本地帳號（不改動 Status / 權限）
        if (!string.IsNullOrWhiteSpace(email))
        {
            user = await context.MyUser
                .FirstOrDefaultAsync(x => x.Email != null && x.Email.ToLower() == email.ToLower());
            if (user is not null)
            {
                user.GoogleId = googleSubject;
                user.OAuthProvider = provider;
                user.UpdateAt = DateTime.Now;
                await context.SaveChangesAsync();
                logger.LogInformation("External login linked Google to existing account. UserId={UserId}.", user.Id);
                return user;
            }
        }

        // 3) 自動建立新帳號（預設停用，待管理者啟用）
        int? defaultRoleId = await context.RoleView
            .Where(x => x.Name == defaultRoleName)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();

        var newUser = new MyUser
        {
            Account = email,
            Name = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
            Email = email,
            Password = string.Empty,
            Salt = null,
            Status = false,
            IsAdmin = false,
            OAuthProvider = provider,
            GoogleId = googleSubject,
            RoleViewId = defaultRoleId,
            CreateAt = DateTime.Now,
            UpdateAt = DateTime.Now,
        };

        await context.MyUser.AddAsync(newUser);
        await context.SaveChangesAsync();

        logger.LogInformation(
            "External login created new disabled account awaiting approval. UserId={UserId}, Email={Email}.",
            newUser.Id, email);
        return newUser;
    }
}
