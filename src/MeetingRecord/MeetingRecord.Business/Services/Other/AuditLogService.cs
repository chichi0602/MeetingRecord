using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 稽核軌跡寫入服務。
///
/// <para>1.0.32：每次寫入都用<b>自己新建的短命 DbContext</b>（同一組 <see cref="DbContextOptions{TContext}"/>），
/// 不再借用呼叫端的 scoped context。以前共用 Blazor 連線的 context：SaveChanges 會順便存掉呼叫端還沒存的變更，
/// 寫入失敗時那筆稽核又會留在追蹤器裡，下一次 SaveChanges 被重送。</para>
/// <para>來源 IP 由這裡向 <see cref="IClientIpProvider"/> 取得，呼叫端不用傳；背景排程沒有 IP，記 null。</para>
/// <para>⚠️ 不要在呼叫端的交易<b>裡面</b>呼叫：新的 context 走另一條連線，SQLite 會等交易的寫入鎖。目前一律在 Commit 之後才寫。</para>
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private const int ClientIpMaxLength = 64;

    private readonly DbContextOptions<BackendDBContext> options;
    private readonly ILogger<AuditLogService> logger;
    private readonly IClientIpProvider? clientIpProvider;

    public AuditLogService(
        DbContextOptions<BackendDBContext> options,
        ILogger<AuditLogService> logger,
        IClientIpProvider? clientIpProvider = null)
    {
        this.options = options;
        this.logger = logger;
        this.clientIpProvider = clientIpProvider;
    }

    public async Task WriteAsync(
        string action,
        bool success = true,
        int? actorUserId = null,
        string? actorAccount = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null)
    {
        try
        {
            var entry = new AuditLog
            {
                OccurredAt = DateTime.UtcNow,
                Action = action,
                Success = success,
                ActorUserId = actorUserId,
                ActorAccount = actorAccount,
                TargetType = targetType,
                TargetId = targetId,
                Detail = detail,
                ClientIp = ResolveClientIp(),
            };

            await using var context = new BackendDBContext(options);
            context.AuditLog.Add(entry);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log. Action={Action}", action);
        }
    }

    /// <summary>取 IP 失敗不能讓稽核整筆寫不進去。</summary>
    private string? ResolveClientIp()
    {
        try
        {
            var ip = clientIpProvider?.GetClientIp();
            return ip is { Length: > ClientIpMaxLength } ? ip[..ClientIpMaxLength] : ip;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve client IP for audit log.");
            return null;
        }
    }
}
