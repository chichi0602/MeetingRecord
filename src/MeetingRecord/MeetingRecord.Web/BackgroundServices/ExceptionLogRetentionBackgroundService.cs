using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// 系統例外紀錄的自動保留期限（0.4.122）：每天刪除「最後發生」超過 <see cref="LogRetentionSettings.ExceptionLogDays"/>
/// 天的紀錄與堆疊檔；設 0 不清。Starter 用排程框架，本專案沒有，改用與 <c>ExchangeRateBackgroundService</c> 相同的迴圈。
///
/// <para>⚠️ 進迴圈前先跑一次（開發慣例速查 §8.1）：只靠 24 小時的延遲，重啟當天整天都不會清。</para>
/// <para>⚠️ 用本地時間：<c>LastOccurredAt</c> 存的是本地時間，拿 UTC 比會差 8 小時。</para>
/// </summary>
public sealed class ExceptionLogRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<ExceptionLogRetentionBackgroundService> logger;

    public ExceptionLogRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<LogRetentionSettings> options,
        ExceptionContextAccessor contextAccessor,
        ILogger<ExceptionLogRetentionBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, nameof(ExceptionLogRetentionBackgroundService), null, null));

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
        var days = options.CurrentValue.ExceptionLogDays;
        if (days <= 0)
        {
            logger.LogDebug("Exception log retention is disabled (ExceptionLogDays=0).");
            return 0;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ExceptionLogService>();
            var removed = await service.PurgeBeforeAsync(now.AddDays(-days));

            logger.LogInformation("Exception log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

            if (removed > 0)
            {
                var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
                await audit.WriteAsync(
                    AuditActions.ExceptionLogAutoPurge,
                    success: true,
                    targetType: "ExceptionLog",
                    targetId: "*",
                    detail: $"rows={removed}; days={days}");
            }

            return removed;
        }
        catch (Exception ex)
        {
            // 背景服務不能因為一次失敗就停掉；這筆錯誤本身會進系統例外紀錄（來源「背景作業」）。
            logger.LogError(ex, "Exception log retention failed. RetentionDays={RetentionDays}", days);
            return 0;
        }
    }
}
