namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 日誌與例外管線的自我監控（0.4.122，移植自 NET10-Blazor-Starter，拿掉 Email 告警的計數）：累計「記錄機制本身出了問題」的次數，
/// 供系統健康監控的「例外紀錄管線」項目判斷。避免記錄機制壞了卻沒人知道。
///
/// 數值都是「本次啟動以來」，重啟歸零。所有方法執行緒安全、不拋出。
///
/// ⚠️ <b>本類別絕不可使用 <see cref="ILogger"/>。</b>它被例外寫入器與 NLog 的內部事件呼叫；
/// 在 NLog 內部事件處理器裡呼叫任何 NLog logger（含 ILogger）會死結或無限遞迴。
/// </summary>
public sealed class LoggingPipelineMonitor
{
    private long writeFailures;
    private long clientErrorsDropped;
    private long nlogInternalErrors;
    private long lastWriteFailureTicks;
    private long lastNLogInternalErrorTicks;

    public void RecordWriteFailure(DateTimeOffset now)
    {
        Interlocked.Increment(ref writeFailures);
        Interlocked.Exchange(ref lastWriteFailureTicks, now.UtcTicks);
    }

    public void RecordClientErrorDropped() => Interlocked.Increment(ref clientErrorsDropped);

    public void RecordNLogInternalError(DateTimeOffset now)
    {
        Interlocked.Increment(ref nlogInternalErrors);
        Interlocked.Exchange(ref lastNLogInternalErrorTicks, now.UtcTicks);
    }

    public LoggingPipelineSnapshot Snapshot() => new(
        Interlocked.Read(ref writeFailures),
        ToTime(Interlocked.Read(ref lastWriteFailureTicks)),
        Interlocked.Read(ref clientErrorsDropped),
        Interlocked.Read(ref nlogInternalErrors),
        ToTime(Interlocked.Read(ref lastNLogInternalErrorTicks)));

    /// <summary>
    /// 訂閱 NLog 的內部事件，累計 Error 以上的內部錯誤（例如寫檔失敗、設定錯誤）。
    /// 事件是行程全域的，<paramref name="lifetime"/> 停止時取消訂閱（整合測試會在同一行程連續啟動多個 host）。
    /// </summary>
    public void Register(IHostApplicationLifetime lifetime, TimeProvider timeProvider)
    {
        void OnInternalEvent(object? sender, NLog.Common.InternalLogEventArgs e)
        {
            // ⚠️ 這裡絕不可呼叫任何 NLog logger（見類別註解），只累加計數。
            if (e.Level >= NLog.LogLevel.Error)
            {
                RecordNLogInternalError(timeProvider.GetUtcNow());
            }
        }

        NLog.Common.InternalLogger.InternalEventOccurred += OnInternalEvent;
        lifetime.ApplicationStopped.Register(() => NLog.Common.InternalLogger.InternalEventOccurred -= OnInternalEvent);
    }

    private static DateTimeOffset? ToTime(long utcTicks)
        => utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);
}

/// <summary>日誌管線在某一時刻的狀態（本次啟動以來的累計）。</summary>
public sealed record LoggingPipelineSnapshot(
    long WriteFailures,
    DateTimeOffset? LastWriteFailureAt,
    long ClientErrorsDropped,
    long NLogInternalErrors,
    DateTimeOffset? LastNLogInternalErrorAt);
