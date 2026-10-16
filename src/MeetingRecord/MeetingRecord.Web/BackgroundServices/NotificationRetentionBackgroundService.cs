using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 站內通知的自動保留期限（1.0.44）。每天執行一次，刪除超過 <see cref="LogRetentionSettings.NotificationDays"/>（預設 90）天的通知；
/// 設成 0 不清。形狀比照 <see cref="AuditLogRetentionBackgroundService"/>，但不寫稽核：通知只是提醒，
/// 真正的修改紀錄在待辦的異動紀錄裡。
/// ⚠️ 通知存本地時間（與待辦一致），門檻也用本地時間——與稽核紀錄（UTC）不同。
/// </summary>
public sealed class NotificationRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<LogRetentionSettings> retentionOptions;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<NotificationRetentionBackgroundService> logger;

    public NotificationRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<LogRetentionSettings> retentionOptions,
        ExceptionContextAccessor contextAccessor,
        ILogger<NotificationRetentionBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.retentionOptions = retentionOptions;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, nameof(NotificationRetentionBackgroundService), null, null));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(DateTime.Now);

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
    public async Task<int> RunOnceAsync(DateTime now)
    {
        var days = retentionOptions.CurrentValue.NotificationDays;
        if (days <= 0)
        {
            logger.LogDebug("Notification retention is disabled (NotificationDays=0).");
            return 0;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var removed = await scope.ServiceProvider.GetRequiredService<UserNotificationService>()
                .PurgeBeforeAsync(now.AddDays(-days));

            logger.LogInformation("Notification retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notification retention failed. RetentionDays={RetentionDays}", days);
            return 0;
        }
    }
}
