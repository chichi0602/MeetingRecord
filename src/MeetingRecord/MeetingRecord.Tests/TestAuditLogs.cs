using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>
/// 測試用的 <see cref="AuditLogService"/>（1.0.32 起寫入服務改用自己新建的 context）：
/// 沿用測試 context 的同一組 options——也就是同一條 in-memory SQLite 連線，寫進去的稽核測試讀得到。
/// </summary>
internal static class TestAuditLogs
{
    public static AuditLogService CreateService(
        BackendDBContext context,
        IClientIpProvider? clientIpProvider = null,
        ILogger<AuditLogService>? logger = null,
        IAuditActorProvider? actorProvider = null)
        => new(OptionsOf(context), logger ?? NullLogger<AuditLogService>.Instance, clientIpProvider, actorProvider);

    /// <summary>業務服務用：<c>WriteForCurrentUserAsync</c> 的操作者取自 <paramref name="actor"/>（1.0.34）。</summary>
    public static AuditLogService CreateService(BackendDBContext context, IAuditActorProvider actor)
        => CreateService(context, clientIpProvider: null, logger: null, actorProvider: actor);

    public static DbContextOptions<BackendDBContext> OptionsOf(BackendDBContext context)
        => (DbContextOptions<BackendDBContext>)context.GetService<IDbContextOptions>();

    /// <summary>DI 版：背景服務測試自己組 ServiceCollection 時註冊這一份，<see cref="AuditLogService"/> 才解析得到。</summary>
    public static DbContextOptions<BackendDBContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
}

/// <summary>測試用的「目前操作者」（1.0.34）。</summary>
internal sealed class TestAuditActor : IAuditActorProvider
{
    private readonly Func<(int? UserId, string? Account)> resolve;

    private TestAuditActor(Func<(int? UserId, string? Account)> resolve)
    {
        this.resolve = resolve;
    }

    /// <summary>固定的操作者。</summary>
    public static TestAuditActor Fixed(int userId, string account) => new(() => (userId, account));

    /// <summary>跟著 <see cref="CurrentUserService"/> 走（比照正式環境的 Blazor 路徑）。</summary>
    public static TestAuditActor From(CurrentUserService currentUserService)
        => new(() => currentUserService.CurrentUser.Id > 0
            ? (currentUserService.CurrentUser.Id, currentUserService.CurrentUser.Account)
            : (null, null));

    public (int? UserId, string? Account) GetActor() => resolve();
}
