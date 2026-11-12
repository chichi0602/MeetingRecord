using Microsoft.Extensions.Logging;

namespace MeetingRecord.Web.Client.Services;

/// <summary>
/// 把頁面上的 .NET 例外送到系統例外紀錄（1.1.54）。
///
/// <para>
/// 瀏覽器的 <c>error</c>／<c>unhandledrejection</c> 由 <c>client-error-reporter.js</c> 攔；但 .NET 例外在 WASM 裡不會觸發這兩個事件：
/// 被 <c>WasmLayout</c> 的 <c>ErrorBoundary</c> 接住的、或算繪時沒人接住的，Blazor 只用 <see cref="ILogger"/> 記錄
/// （元件框架的類別、Error 以上）。<see cref="ClientErrorLoggerProvider"/> 收到這些記錄就交給這裡，
/// 經 <see cref="ClientErrorReporter"/> 送到 <c>POST api/v1/client-errors</c>，種類 <c>dotnet</c>。
/// </para>
///
/// <para>
/// 登入狀態確認前（<c>WasmLayout</c> 還沒 <see cref="Attach"/>）發生的先暫存（上限 <see cref="MaxBuffered"/> 筆），接上時一次送出——同 JS 端的做法。
/// 送出失敗一律吞掉（<see cref="ClientErrorReporter"/> 已處理），不可再記 Error，否則會自己觸發自己。
/// </para>
/// </summary>
public sealed class DotNetErrorForwarder
{
    public const string Kind = "dotnet";
    public const int MaxBuffered = 20;
    private const int MaxMessageLength = 1000;
    private const int MaxStackLength = 4000;

    private readonly object gate = new();
    private readonly List<(string Message, string Stack, string? Path)> buffer = [];
    private Func<string, string, string, string?, Task>? sink;
    private Func<string>? currentPath;

    /// <summary>登入狀態確認後由 <c>WasmLayout</c> 呼叫一次；再呼叫會取代前一個。</summary>
    /// <param name="send">送出一筆：種類、訊息、堆疊、頁面路徑。</param>
    /// <param name="pathProvider">目前頁面的路徑（只取路徑，伺服器還會再去掉查詢字串）。</param>
    public void Attach(Func<string, string, string, string?, Task> send, Func<string> pathProvider)
    {
        List<(string Message, string Stack, string? Path)> pending;
        lock (gate)
        {
            sink = send;
            currentPath = pathProvider;
            pending = [.. buffer];
            buffer.Clear();
        }

        foreach (var item in pending)
        {
            _ = send(Kind, item.Message, item.Stack, item.Path ?? pathProvider());
        }
    }

    /// <summary>記下一個例外：已接上就送出，否則暫存。</summary>
    public void Capture(Exception exception)
    {
        var message = Truncate($"{exception.GetType().FullName}: {exception.Message}", MaxMessageLength);
        var stack = Truncate(exception.ToString(), MaxStackLength);

        Func<string, string, string, string?, Task>? send;
        string? path;
        lock (gate)
        {
            send = sink;
            if (send is null)
            {
                if (buffer.Count < MaxBuffered)
                {
                    buffer.Add((message, stack, null));
                }

                return;
            }

            path = SafePath();
        }

        _ = send(Kind, message, stack, path);
    }

    /// <summary>目前暫存、還沒送出的筆數。</summary>
    public int BufferedCount
    {
        get
        {
            lock (gate)
            {
                return buffer.Count;
            }
        }
    }

    private string? SafePath()
    {
        try
        {
            return currentPath?.Invoke();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// 只轉送 Blazor 元件框架（類別以 <c>Microsoft.AspNetCore.Components</c> 開頭）記下、Error 以上、帶例外的記錄：
/// <c>ErrorBoundary</c> 接住的例外、算繪時未處理的例外。頁面自己記的錯誤（例如 API 失敗）不轉送——那些在伺服器端已經有紀錄。
/// </summary>
public sealed class ClientErrorLoggerProvider(DotNetErrorForwarder forwarder) : ILoggerProvider
{
    private const string ComponentsCategory = "Microsoft.AspNetCore.Components";

    public ILogger CreateLogger(string categoryName)
        => categoryName.StartsWith(ComponentsCategory, StringComparison.Ordinal) ? new ForwardingLogger(forwarder) : NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class ForwardingLogger(DotNetErrorForwarder forwarder) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                forwarder.Capture(exception);
            }
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
