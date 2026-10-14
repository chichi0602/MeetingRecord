using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 把「程式碼記下來的例外」收進系統例外紀錄的 <see cref="ILoggerProvider"/>。
///
/// 為什麼掛在 ILogger 管線而不是掛「未處理例外」：本專案各服務層有大量
/// <c>catch (Exception ex)</c>，多數在服務層記完 <c>LogError</c> 後就回傳
/// <c>VerifyRecordResult</c>，<b>例外不再往上拋</b>。只掛 error boundary 或
/// API filter 會完全收不到它們 —— 而那正是「系統怪怪的」的主要來源。
/// 掛在這裡，現有與日後新寫的 catch 全都自動涵蓋，一行都不用改。
///
/// 與 NLog 並存：NLog 仍照常把完整日誌寫進檔案，本 provider 只是額外收一份摘要進資料庫。
/// </summary>
public sealed class ExceptionLogProvider : ILoggerProvider
{
    private readonly ChannelWriter<ExceptionLogEntry> writer;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ConcurrentDictionary<string, ILogger> loggers = new(StringComparer.Ordinal);

    /// <summary>
    /// 已收錄過的例外實例。同一個例外常被記兩次以上 —— 例如 <c>UseHttpRequestLogging</c> 記完重拋，
    /// 框架的 ExceptionHandlerMiddleware／DeveloperExceptionPageMiddleware 再記一次；或「記錄後重拋」的服務。
    /// 兩次的訊息樣板不同，會變成兩個簽章、兩列紀錄。以實例去重，只收第一次（最內層、情境最完整）。
    /// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/>：不延長例外的生命週期，執行緒安全。
    /// NLog 日誌檔不受影響，照常每次都寫。
    /// </summary>
    private readonly ConditionalWeakTable<Exception, object> capturedExceptions = new();

    public ExceptionLogProvider(
        ChannelWriter<ExceptionLogEntry> writer,
        ExceptionContextAccessor contextAccessor)
    {
        this.writer = writer;
        this.contextAccessor = contextAccessor;
    }

    /// <summary>
    /// 被丟棄的筆數（Channel 滿載）。由寫入器在停止時輸出。
    /// </summary>
    public static long DroppedCount => droppedCount;

    /// <summary>例外佇列容量（ServiceCollectionExtensions 建立 Channel 時使用；健康監控據此算使用率）。</summary>
    public const int QueueCapacity = 1000;

    private static long droppedCount;

    public ILogger CreateLogger(string categoryName)
        => loggers.GetOrAdd(categoryName, name => new ExceptionCapturingLogger(name, writer, contextAccessor, capturedExceptions));

    public void Dispose() => loggers.Clear();

    internal static void IncrementDropped() => Interlocked.Increment(ref droppedCount);

    /// <summary>
    /// 只做一件事：判斷該不該收，該收就組出 <see cref="ExceptionLogEntry"/> 丟進佇列。
    /// 絕不阻塞、絕不拋出。
    /// </summary>
    private sealed class ExceptionCapturingLogger : ILogger
    {
        /// <summary>
        /// 本子系統自己的記錄器字首。防遞迴的第一道防線：
        /// 寫入過程若記了錯誤，不能再被收進來，否則「失敗 → 記錄 → 再失敗」無限循環。
        /// </summary>
        private const string OwnLoggerPrefix = "MeetingRecord.Web.Diagnostics.ExceptionLog";

        private const string OwnServicePrefix = "MeetingRecord.Business.Services.DataAccess.ExceptionLogService";
        private const string OwnStorePrefix = "MeetingRecord.Business.Services.Other.ExceptionStackFileStore";

        private static readonly object CapturedMarker = new();

        private readonly string categoryName;
        private readonly ChannelWriter<ExceptionLogEntry> writer;
        private readonly ExceptionContextAccessor contextAccessor;
        private readonly ConditionalWeakTable<Exception, object> capturedExceptions;

        public ExceptionCapturingLogger(
            string categoryName,
            ChannelWriter<ExceptionLogEntry> writer,
            ExceptionContextAccessor contextAccessor,
            ConditionalWeakTable<Exception, object> capturedExceptions)
        {
            this.categoryName = categoryName;
            this.writer = writer;
            this.contextAccessor = contextAccessor;
            this.capturedExceptions = capturedExceptions;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// 只收 Error 以上。Warning 多半是「已處理、降級可用」（例如稽核寫入失敗），
        /// 收進來只是噪音。這個門檻刻意是程式常數，不做成設定鍵。
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                if (ShouldCapture(logLevel, exception) == false)
                {
                    return;
                }

                var context = contextAccessor.Current;
                var entry = new ExceptionLogEntry
                {
                    ExceptionType = exception!.GetType().FullName ?? exception.GetType().Name,
                    Message = exception.Message,
                    StackTrace = exception.ToString(),
                    Source = context?.Source ?? ExceptionSources.Unknown,
                    Page = context?.Page,
                    Operation = ExtractMessageTemplate(state),
                    LoggerName = categoryName,
                    Account = context?.Account,
                    UserId = context?.UserId,
                    OccurredAt = DateTime.Now,
                    TraceId = TraceCode.Current,
                    IsCritical = logLevel >= LogLevel.Critical,
                };

                // TryWrite 不阻塞。佇列滿了就丟棄 —— 與 nlog.config 的
                // AsyncWrapper overflowAction="Discard" 同一種取捨：寧可漏記，不可拖垮主流程。
                // Channel 是 Wait 模式，滿載時 TryWrite 才會回傳 false（DropWrite 永遠回 true，計數會失靈）。
                if (writer.TryWrite(entry) == false)
                {
                    IncrementDropped();
                }
            }
            catch (Exception)
            {
                // 記錄管線本身絕不可拋出，否則會把呼叫端的正常流程一起弄壞。
            }
        }

        private bool ShouldCapture(LogLevel logLevel, Exception? exception)
        {
            if (logLevel < LogLevel.Error || exception is null)
            {
                return false;
            }

            // 使用者取消、正常關機等刻意忽略的情況不該出現在本頁。
            if (exception is OperationCanceledException)
            {
                return false;
            }

            // 防遞迴第一道：本子系統自己的記錄器。
            if (categoryName.StartsWith(OwnLoggerPrefix, StringComparison.Ordinal) ||
                categoryName.StartsWith(OwnServicePrefix, StringComparison.Ordinal) ||
                categoryName.StartsWith(OwnStorePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            // 防遞迴第二道：寫入器執行期間全程開啟抑制旗標。
            if (contextAccessor.IsSuppressed)
            {
                return false;
            }

            // 同一個例外實例只收一次（一次發生計一次）。TryAdd 失敗代表先前已收過。
            return capturedExceptions.TryAdd(exception, CapturedMarker);
        }

        /// <summary>
        /// 取出訊息「樣板」，例如 <c>Failed to create category. Name={CategoryName}</c>。
        ///
        /// ⚠️ <b>這是整個設計最容易寫錯的地方。</b>必須取 {OriginalFormat}，
        /// 不能用 formatter(state, exception) 的結果 —— 後者會算成
        /// <c>Failed to create category. Name=技術文件</c>，
        /// 於是每個分類名稱都變成一個新簽章，列數立刻失控。
        /// </summary>
        private static string? ExtractMessageTemplate<TState>(TState state)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                for (var index = 0; index < values.Count; index++)
                {
                    if (string.Equals(values[index].Key, "{OriginalFormat}", StringComparison.Ordinal))
                    {
                        return values[index].Value?.ToString();
                    }
                }
            }

            return null;
        }
    }
}
