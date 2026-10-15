using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 稽核紀錄的自動保留期限（1.0.32）。每天執行一次，刪除超過 <see cref="LogRetentionSettings.AuditLogDays"/>（預設 365）天的紀錄；
/// 設成 0 不清。形狀比照 <see cref="AiLogRetentionBackgroundService"/>：進迴圈前先跑一次、有刪才寫稽核。
/// ⚠️ 稽核紀錄存 UTC，門檻也用 UTC（其他紀錄表存本地時間，別照抄它們的 <c>DateTime.Now</c>）。
/// </summary>
public sealed class AuditLogRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<LogRetentionSettings> retentionOptions;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<AuditLogRetentionBackgroundService> logger;

    public AuditLogRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<LogRetentionSettings> retentionOptions,
        ExceptionContextAccessor contextAccessor,
        ILogger<AuditLogRetentionBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.retentionOptions = retentionOptions;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, nameof(AuditLogRetentionBackgroundService), null, null));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(DateTime.UtcNow);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>跑一次清理，回傳刪除筆數。獨立成 public 方法讓測試能直接驗證。</summary>
    public async Task<int> RunOnceAsync(DateTime utcNow)
    {
        var days = retentionOptions.CurrentValue.AuditLogDays;
        if (days <= 0)
        {
            logger.LogDebug("Audit log retention is disabled (AuditLogDays=0).");
            return 0;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AuditLogQueryService>();
            var removed = await service.PurgeBeforeAsync(utcNow.AddDays(-days));

            logger.LogInformation("Audit log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

            // 刪完才寫，這一筆才會留在表裡：「誰（哪個排程）清了稽核紀錄」永遠答得出來。
            if (removed > 0)
            {
                await scope.ServiceProvider.GetRequiredService<IAuditLogService>().WriteAsync(
                    AuditActions.AuditAutoPurge,
                    success: true,
                    targetType: "AuditLog",
                    targetId: "*",
                    detail: $"rows={removed}; days={days}");
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit log retention failed. RetentionDays={RetentionDays}", days);
            return 0;
        }
    }
}
