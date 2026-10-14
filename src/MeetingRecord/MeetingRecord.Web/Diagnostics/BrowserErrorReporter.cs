using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 接收瀏覽器端錯誤（0.4.122，移植自 NET10-Blazor-Starter）：<c>wwwroot/js/client-error-reporter.js</c> 經 circuit 呼叫 <see cref="Report"/>。
///
/// Scoped（＝每個 circuit 一份），頻率限制天生就是「每個 circuit」。由 MainLayout 在登入後註冊，
/// 所以只有已登入、有 circuit 的頁面會回報；帳號取自 <see cref="CurrentUserService"/>（MainLayout 已驗證過）。
///
/// 前端送來的東西一律不信任：長度在這裡再截一次、頁面只取路徑並截短。
/// 以 Error 記錄並帶 <see cref="BrowserScriptException"/>，進系統例外紀錄（來源「瀏覽器」）。
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
    private readonly CurrentUserService currentUserService;
    private readonly IOptionsMonitor<ClientErrorReportingSettings> options;
    private readonly LoggingPipelineMonitor monitor;
    private readonly TimeProvider timeProvider;

    private readonly Queue<DateTimeOffset> recent = new();
    private readonly object gate = new();

    public BrowserErrorReporter(
        ILogger<BrowserErrorReporter> logger,
        ExceptionContextAccessor contextAccessor,
        CurrentUserService currentUserService,
        IOptionsMonitor<ClientErrorReportingSettings> options,
        LoggingPipelineMonitor monitor,
        TimeProvider timeProvider)
    {
        this.logger = logger;
        this.contextAccessor = contextAccessor;
        this.currentUserService = currentUserService;
        this.options = options;
        this.monitor = monitor;
        this.timeProvider = timeProvider;
    }

    public bool IsEnabled => options.CurrentValue.Enabled;

    /// <returns>是否記錄了（供測試斷言）。</returns>
    [JSInvokable]
    public bool Report(string? kind, string? message, string? stack, string? source, string? path)
    {
        var settings = options.CurrentValue;
        if (settings.Enabled == false)
        {
            return false;
        }

        if (TryAcquire(settings.MaxPerCircuitPerMinute) == false)
        {
            monitor.RecordClientErrorDropped();
            return false;
        }

        var safeKind = kind is "unhandledrejection" ? "unhandledrejection" : "error";
        var exception = new BrowserScriptException(
            safeKind,
            Truncate(string.IsNullOrWhiteSpace(message) ? "(no message)" : message, MaxMessageLength),
            Truncate(stack, MaxStackLength),
            Truncate(source, MaxSourceLength));

        var user = currentUserService.CurrentUser;
        var previous = contextAccessor.Current;
        try
        {
            contextAccessor.Set(new ExceptionContext(
                ExceptionSources.Browser,
                NormalizePath(path),
                user.Id > 0 ? user.Account : null,
                user.Id > 0 ? user.Id : null));

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

    private bool TryAcquire(int maxPerMinute)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
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
