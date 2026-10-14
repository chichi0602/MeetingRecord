using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;
using NLog;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 程序層級的最後一道網：<see cref="TaskScheduler.UnobservedTaskException"/> 與
/// <see cref="AppDomain.UnhandledException"/>。
///
/// 為什麼需要：<c>_ = messageService.SuccessAsync(...)</c> 這類「射後不理」的 Task 失敗時沒有任何人 await，
/// 例外只會在 GC 回收時默默觸發 UnobservedTaskException；背景執行緒的未處理例外則會直接結束程序。
/// 兩者都不經過請求管線、錯誤邊界或 API 篩選器，0.4.121 以前完全不留紀錄。
///
/// ⚠️ 兩個事件都是<b>行程全域</b>的。整合測試會在同一個行程連續啟動多個 host，
/// 所以一定要在 ApplicationStopped 取消訂閱，否則舊 host 的處理器會一直累積。
/// </summary>
public sealed class ProcessExceptionHooks
{
    private readonly ILogger<ProcessExceptionHooks> logger;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptions<SystemSettings> systemSettings;

    public ProcessExceptionHooks(
        ILogger<ProcessExceptionHooks> logger,
        ExceptionContextAccessor contextAccessor,
        IOptions<SystemSettings> systemSettings)
    {
        this.logger = logger;
        this.contextAccessor = contextAccessor;
        this.systemSettings = systemSettings;
    }

    public void Register(IHostApplicationLifetime lifetime)
    {
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        lifetime.ApplicationStopped.Register(() =>
        {
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        });
    }

    /// <summary>
    /// 在終結器執行緒上觸發。記成 Error（自動進例外紀錄，來源「系統」），並標記為已觀察。
    /// 情境用完要還原，否則會污染同一條執行緒上之後的工作。
    /// </summary>
    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var previous = contextAccessor.Current;
        try
        {
            contextAccessor.Set(new ExceptionContext(ExceptionSources.Process, null, null, null));
            logger.LogError(e.Exception, "Unobserved task exception.");
        }
        catch (Exception ex)
        {
            // 記錄失敗也不能讓終結器執行緒出事；改留在 NLog 內部日誌。
            NLog.Common.InternalLogger.Warn(ex, "Failed to log an unobserved task exception.");
        }
        finally
        {
            RestoreContext(previous);
            e.SetObserved();
        }
    }

    /// <summary>
    /// 程序即將結束：例外紀錄的背景寫入器來不及寫資料庫，所以先寫補登檔，下次啟動時補進例外紀錄。
    /// 記錄日誌時開啟抑制旗標，避免「佇列剛好寫進去＋補登」變成兩列。
    /// </summary>
    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
        {
            return;
        }

        CrashMarkerStore.Write(
            systemSettings.Value.ExternalFileSystem.ExceptionPath,
            exception,
            ExceptionSources.Process,
            "Unhandled exception in the process. IsTerminating={IsTerminating}",
            typeof(ProcessExceptionHooks).FullName!);

        try
        {
            using var suppression = contextAccessor.Suppress();
            logger.LogCritical(exception, "Unhandled exception in the process. IsTerminating={IsTerminating}", e.IsTerminating);
            LogManager.Flush(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            // 程序正在結束，補登檔已寫，這裡失敗只少了日誌檔的一行。
            NLog.Common.InternalLogger.Warn(ex, "Failed to log an unhandled process exception.");
        }
    }

    private void RestoreContext(ExceptionContext? previous)
    {
        if (previous is null)
        {
            contextAccessor.Clear();
        }
        else
        {
            contextAccessor.Set(previous);
        }
    }
}
