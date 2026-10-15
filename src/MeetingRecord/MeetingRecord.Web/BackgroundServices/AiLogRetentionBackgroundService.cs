using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Web.BackgroundServices;

/// <summary>
/// AI 對話紀錄與用量帳本的自動保留期限（1.0.29）。每天執行一次：
/// <list type="bullet">
/// <item>AI 對話紀錄：刪除超過 <see cref="AiCallLogSettings.RetentionDays"/>（預設 90）天的紀錄與內容檔。停用記錄時照樣清。</item>
/// <item>用量帳本：<see cref="LogRetentionSettings.AiUsageLogDays"/> 大於 0 時才清（預設 0＝永久保留）。</item>
/// </list>
/// 形狀比照 <see cref="ExceptionLogRetentionBackgroundService"/>：進迴圈前先跑一次、用本地時間、有刪才寫稽核。
/// </summary>
public sealed class AiLogRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<LogRetentionSettings> retentionOptions;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<AiLogRetentionBackgroundService> logger;

    public AiLogRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<LogRetentionSettings> retentionOptions,
        ExceptionContextAccessor contextAccessor,
        ILogger<AiLogRetentionBackgroundService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.retentionOptions = retentionOptions;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, nameof(AiLogRetentionBackgroundService), null, null));

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

    /// <summary>跑一次清理，回傳（對話紀錄、帳本）各刪除幾筆。獨立成 public 方法讓測試能直接驗證。</summary>
    public async Task<(int CallLogs, int UsageRows)> RunOnceAsync(DateTime now)
    {
        var callLogs = await PurgeCallLogsAsync(now);
        var usageRows = await PurgeUsageLedgerAsync(now);
        return (callLogs, usageRows);
    }

    private async Task<int> PurgeCallLogsAsync(DateTime now)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AiCallLogService>();
            var removed = await service.PurgeExpiredAsync(now);

            logger.LogInformation(
                "AI call log retention completed. Rows={Rows}, RetentionDays={RetentionDays}",
                removed,
                service.RetentionDays);

            if (removed > 0)
            {
                await WriteAuditAsync(scope, AuditActions.AiCallLogAutoPurge, "AiCallLog", $"rows={removed}; days={service.RetentionDays}");
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI call log retention failed.");
            return 0;
        }
    }

    private async Task<int> PurgeUsageLedgerAsync(DateTime now)
    {
        var days = retentionOptions.CurrentValue.AiUsageLogDays;
        if (days <= 0)
        {
            logger.LogDebug("AI usage ledger retention is disabled (AiUsageLogDays=0).");
            return 0;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AiUsageAnalysisService>();
            var removed = await service.PurgeBeforeAsync(now.AddDays(-days));

            logger.LogInformation("AI usage ledger retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

            if (removed > 0)
            {
                await WriteAuditAsync(scope, AuditActions.AiUsageAutoPurge, "AiUsageLog", $"rows={removed}; days={days}");
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI usage ledger retention failed. RetentionDays={RetentionDays}", days);
            return 0;
        }
    }

    private static Task WriteAuditAsync(IServiceScope scope, string action, string targetType, string detail)
        => scope.ServiceProvider.GetRequiredService<IAuditLogService>().WriteAsync(
            action,
            success: true,
            targetType: targetType,
            targetId: "*",
            detail: detail);
}
