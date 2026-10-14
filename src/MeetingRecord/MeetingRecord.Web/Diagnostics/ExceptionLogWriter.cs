using System.Diagnostics;
using System.Threading.Channels;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 系統例外紀錄的背景寫入器（0.4.122，移植自 NET10-Blazor-Starter，拿掉 Email 告警）。
///
/// 為什麼要有它：寫資料庫不能發生在 <c>logger.LogError()</c> 的呼叫執行緒上。
/// AI 主機逾時那種「短時間重複數百次」的情境，若每次都同步寫一次 DB，
/// 會直接拖垮正在等待的使用者。改成「入列即返回、背景慢慢寫」。
///
/// 單一消費者還順帶解決並發 upsert 競爭：同一個簽章不會有兩個執行緒同時想新增。
///
/// ⚠️ <b>本類別絕不可使用 <see cref="ILogger"/>。</b>它位在例外記錄管線的最末端，
/// 走 ILogger 會讓自己的失敗再被收進管線，形成無限遞迴。需要留話時改用
/// NLog 的 InternalLogger（Program.cs 啟動時已設定好輸出檔）。
/// </summary>
public sealed class ExceptionLogWriter : BackgroundService
{
    /// <summary>
    /// 關機時清空佇列的時間上限。須遠小於主機的關機逾時（預設 30 秒），
    /// 否則資料庫卡住時會拖住整個關機流程。
    /// </summary>
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ChannelReader<ExceptionLogEntry> reader;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly TimeSpan drainTimeout;
    private readonly LoggingPipelineMonitor? monitor;

    public ExceptionLogWriter(
        ChannelReader<ExceptionLogEntry> reader,
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        LoggingPipelineMonitor monitor)
        : this(reader, scopeFactory, contextAccessor, DefaultDrainTimeout, monitor)
    {
    }

    internal ExceptionLogWriter(
        ChannelReader<ExceptionLogEntry> reader,
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        TimeSpan drainTimeout,
        LoggingPipelineMonitor? monitor = null)
    {
        this.reader = reader;
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
        this.drainTimeout = drainTimeout;
        this.monitor = monitor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>
    /// 主迴圈。獨立成 internal 方法，讓測試能以「已取消的 token」直接驗證關機時的清空行為。
    /// </summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        NLog.Common.InternalLogger.Info("ExceptionLogWriter started.");

        try
        {
            await foreach (var entry in reader.ReadAllAsync(stoppingToken))
            {
                await WriteOneAsync(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常關機。佇列裡可能還有剛記下的例外（包括關機過程本身的錯誤），
            // 在這裡直接結束的話，它們就此遺失。
            await DrainAsync();
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Error(ex, "ExceptionLogWriter stopped unexpectedly.");
        }
        finally
        {
            NLog.Common.InternalLogger.Info(
                "ExceptionLogWriter stopped. DroppedEntries={0}", ExceptionLogProvider.DroppedCount);
        }
    }

    /// <summary>
    /// 把佇列中剩餘的項目寫完，最多 <see cref="drainTimeout"/>。逾時仍寫不完的筆數輸出到 InternalLogger。
    /// </summary>
    private async Task DrainAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var written = 0;

        while (stopwatch.Elapsed < drainTimeout && reader.TryRead(out var entry))
        {
            await WriteOneAsync(entry);
            written++;
        }

        if (reader.CanCount && reader.Count > 0)
        {
            NLog.Common.InternalLogger.Warn(
                "ExceptionLogWriter drain timed out. Written={0}, Remaining={1}", written, reader.Count);
        }
        else if (written > 0)
        {
            NLog.Common.InternalLogger.Info("ExceptionLogWriter drained {0} entries on shutdown.", written);
        }
    }

    private async Task WriteOneAsync(ExceptionLogEntry entry)
    {
        // 抑制旗標涵蓋整個寫入過程：期間任何 LogError 都不會再被收進管線。
        using var suppression = contextAccessor.Suppress();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ExceptionLogService>();
            var outcome = await service.RecordAsync(entry);

            // RecordAsync 自己吞掉所有錯誤並回傳 null —— 寫入失敗只能從這裡看出來。
            if (outcome is null)
            {
                monitor?.RecordWriteFailure(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            // 絕不使用 ILogger（見類別註解）。
            NLog.Common.InternalLogger.Warn(ex, "Failed to persist an exception log entry.");
            monitor?.RecordWriteFailure(DateTimeOffset.UtcNow);
        }
    }
}
