using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Business.Services.Other;

public sealed class AuditLogService : IAuditLogService
{
    private readonly BackendDBContext context;
    private readonly ILogger<AuditLogService> logger;

    public AuditLogService(BackendDBContext context, ILogger<AuditLogService> logger)
    {
        this.context = context;
        this.logger = logger;
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
            };

            await context.AuditLog.AddAsync(entry);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log. Action={Action}", action);
        }
    }
}
