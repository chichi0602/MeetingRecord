using Microsoft.Extensions.Options;
using MeetingRecord.Web.Configuration;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 接收瀏覽器端錯誤（0.4.122，移植自 NET10-Blazor-Starter）。
///
/// <para>
/// 1.1.47 起經 API 回報（<c>POST api/v1/client-errors</c>，<c>ClientErrorController</c>）：WASM 頁的
/// <c>wwwroot/js/client-error-reporter.js</c> 捕捉 JS 錯誤後交給 WASM 端的回報器送出；1.1.54 起頁面上的 .NET 例外
/// 由 <c>DotNetErrorForwarder</c> 走同一支 API（種類 <c>dotnet</c>）。只有登入者能回報（API 要登入），匿名者無從灌資料。
/// </para>
///
/// <para>
/// Singleton，**依帳號限流**：每個帳號每分鐘最多 <see cref="ClientErrorReportingSettings.MaxPerCircuitPerMinute"/> 筆
/// （設定名稱沿用 circuit 時代的，避免改到已部署的設定檔），超過的丟棄並計數。
/// </para>
///
/// <para>
/// 前端送來的東西一律不信任：長度在這裡再截一次、頁面只取路徑並截短。
/// 以 Error 記錄並帶 <see cref="BrowserScriptException"/>，進系統例外紀錄（來源「瀏覽器」）。
/// </para>
/// </summary>
public sealed class BrowserErrorReporter
{
    private const int MaxMessageLength = 1000;
    private const int MaxStackLength = 4000;
    private const int MaxSourceLength = 500;
    private const int MaxPathLength = 200;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ILogger<BrowserErrorReporter> logger;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptionsMonitor<ClientErrorReportingSettings> options;
    private readonly LoggingPipelineMonitor monitor;
    private readonly TimeProvider timeProvider;

    private readonly Dictionary<int, Queue<DateTimeOffset>> recentByUser = [];
    private readonly object gate = new();

    public BrowserErrorReporter(
        ILogger<BrowserErrorReporter> logger,
        ExceptionContextAccessor contextAccessor,
        IOptionsMonitor<ClientErrorReportingSettings> options,
        LoggingPipelineMonitor monitor,
        TimeProvider timeProvider)
    {
        this.logger = logger;
        this.contextAccessor = contextAccessor;
        this.options = options;
        this.monitor = monitor;
        this.timeProvider = timeProvider;
    }

    public bool IsEnabled => options.CurrentValue.Enabled;

    /// <summary>記錄一筆瀏覽器錯誤。</summary>
    /// <param name="userId">回報者（API 呼叫者）；限流以它為單位。</param>
    /// <param name="account">回報者帳號，寫進系統例外紀錄的「使用者」。</param>
    /// <returns>是否記錄了（停用、超過上限時為 false）。</returns>
    public bool Report(int userId, string? account, string? kind, string? message, string? stack, string? source, string? path)
    {
        var settings = options.CurrentValue;
        if (settings.Enabled == false)
        {
            return false;
        }

        if (TryAcquire(userId, settings.MaxPerCircuitPerMinute) == false)
        {
            monitor.RecordClientErrorDropped();
            return false;
        }

        var safeKind = kind is "unhandledrejection" or "dotnet" ? kind : "error";
        var exception = new BrowserScriptException(
            safeKind,
            Truncate(string.IsNullOrWhiteSpace(message) ? "(no message)" : message, MaxMessageLength),
            Truncate(stack, MaxStackLength),
            Truncate(source, MaxSourceLength));

        var previous = contextAccessor.Current;
        try
        {
            contextAccessor.Set(new ExceptionContext(
                ExceptionSources.Browser,
                NormalizePath(path),
                string.IsNullOrWhiteSpace(account) ? null : account,
                userId > 0 ? userId : null));

            // 樣板固定，簽章才不會因參數不同而失控；訊息差異已在例外本身。
            logger.LogError(exception, "Browser script error. Kind={Kind}", safeKind);
        }
        finally
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

        return true;
    }

    private bool TryAcquire(int userId, int maxPerMinute)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (!recentByUser.TryGetValue(userId, out var recent))
            {
                recent = new Queue<DateTimeOffset>();
                recentByUser[userId] = recent;
            }

            while (recent.Count > 0 && now - recent.Peek() >= Window)
            {
                recent.Dequeue();
            }

            if (recent.Count >= maxPerMinute)
            {
                return false;
            }

            recent.Enqueue(now);
            return true;
        }
    }

    /// <summary>只留路徑：去掉查詢字串與片段（可能含個資或 token），確保以 / 開頭，並截短。</summary>
    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var cut = path.IndexOfAny(['?', '#']);
        var value = (cut >= 0 ? path[..cut] : path).Trim();
        if (value.StartsWith('/') == false)
        {
            value = "/" + value;
        }

        return Truncate(value, MaxPathLength);
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];
}
