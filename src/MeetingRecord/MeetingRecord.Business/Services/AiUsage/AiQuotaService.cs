using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>上限的範圍。</summary>
public enum AiQuotaScope
{
    /// <summary>全系統每月。</summary>
    Global = 0,

    /// <summary>每位使用者每月。</summary>
    User = 1,
}

/// <summary>某一個上限目前的狀態。金額為換算後的目標幣別（預設新台幣）。</summary>
public sealed record AiQuotaStatus(
    AiQuotaScope Scope,
    int Limit,
    decimal Used,
    DateTime PeriodStart,
    DateTime ResetAt)
{
    /// <summary>有設上限（0＝不限制）。</summary>
    public bool IsLimited => Limit > 0;

    public bool IsReached => IsLimited && Used >= Limit;

    /// <summary>用量／上限；不限制時為 0。</summary>
    public double Ratio => IsLimited ? (double)(Used / Limit) : 0d;

    /// <summary>達八成但還沒到上限。</summary>
    public bool IsWarning => IsLimited && !IsReached && Ratio >= 0.8;

    public string Label => Scope == AiQuotaScope.Global ? "全系統每月" : "每人每月";
}

/// <summary>
/// 已達 AI 用量上限（1.0.29）。繼承 <see cref="InvalidOperationException"/>：既有「顯示錯誤訊息」的 catch 照常運作；
/// 需要區分的地方（背景工作、畫面）另外 catch 這個型別，改記 Warning、不進系統例外紀錄。
/// </summary>
public sealed class AiQuotaExceededException(AiQuotaStatus status) : InvalidOperationException(BuildMessage(status))
{
    public AiQuotaStatus Status { get; } = status;

    /// <summary>給使用者看的訊息，也寫進會議的轉錄／紀錄失敗原因。</summary>
    public static string BuildMessage(AiQuotaStatus status)
        => $"已達 AI 用量上限（{status.Label} {status.Limit:N0} 元，本月已用 {status.Used:N0} 元），"
           + $"{status.ResetAt:M/d} 起恢復；如需提高上限請聯絡系統管理員。";
}

/// <summary>
/// AI 用量上限（1.0.29，移植自 NET10-Blazor-Starter 的 AiQuotaService，只留每月上限）。
///
/// <para>
/// 用量＝本月用量帳本的估算金額，依<b>每一列自己的匯率</b>換算成目標幣別後加總：
/// 有金額但沒有匯率的列改用目前匯率（寧可多算，不要少算）；未設定單價的列算 0。
/// ⚠️ 一律把列讀進記憶體再加總——SQLite 把 decimal 存成 TEXT，在 SQL 端 SUM 會算錯。
/// </para>
///
/// <para>
/// 讀取失敗時<b>放行</b>並記錯誤（同 Starter）：上限是成本護欄，不該因為一次查詢失敗就讓全站的 AI 功能停擺。
/// 多個呼叫同時進行時都會通過檢查，所以實際用量可能略超過上限（寫在文件裡，不加鎖）。
/// </para>
/// </summary>
public interface IAiQuotaService
{
    /// <summary>回傳第一個已達上限的狀態；都沒達到（或都沒設上限）回 null。</summary>
    Task<AiQuotaStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default);

    /// <summary>全系統每月、以及（有傳 userId 時）這位使用者本月的狀態。</summary>
    Task<IReadOnlyList<AiQuotaStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default);

    /// <summary>每人每月上限下，本月用量最高的使用者（給 Token 用量頁的上限列）。</summary>
    Task<IReadOnlyList<AiQuotaUserUsage>> GetTopUsersAsync(int take, CancellationToken cancellationToken = default);
}

/// <summary>一位使用者本月的用量。</summary>
public sealed record AiQuotaUserUsage(int UserId, string? UserName, decimal Used);

public sealed class AiQuotaService : IAiQuotaService
{
    private readonly BackendDBContext context;
    private readonly IOptionsMonitor<AiQuotaSettings> settings;
    private readonly ExchangeRateCache exchangeRates;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AiQuotaService> logger;

    public AiQuotaService(
        BackendDBContext context,
        IOptionsMonitor<AiQuotaSettings> settings,
        ExchangeRateCache exchangeRates,
        TimeProvider timeProvider,
        ILogger<AiQuotaService> logger)
    {
        this.context = context;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<AiQuotaStatus?> CheckAsync(int? userId, CancellationToken cancellationToken = default)
    {
        var current = settings.CurrentValue;
        if (current.GlobalMonthlyTwd <= 0 && current.PerUserMonthlyTwd <= 0)
        {
            return null;
        }

        try
        {
            var statuses = await GetStatusCoreAsync(current, userId, cancellationToken);
            return statuses.FirstOrDefault(x => x.IsReached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI quota check failed; the call is allowed. UserId={UserId}", userId);
            return null;
        }
    }

    public Task<IReadOnlyList<AiQuotaStatus>> GetStatusAsync(int? userId, CancellationToken cancellationToken = default)
        => GetStatusCoreAsync(settings.CurrentValue, userId, cancellationToken);

    public async Task<IReadOnlyList<AiQuotaUserUsage>> GetTopUsersAsync(int take, CancellationToken cancellationToken = default)
    {
        var (monthStart, _) = CurrentMonth();
        var rows = await LoadRowsAsync(monthStart, userId: null, cancellationToken);

        return rows
            .Where(x => x.UserId is not null)
            .GroupBy(x => x.UserId!.Value)
            .Select(g => new AiQuotaUserUsage(
                g.Key,
                g.OrderByDescending(x => x.OccurredAt).Select(x => x.UserName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                g.Sum(x => ToTarget(x.EstimatedCost, x.ExchangeRate))))
            .Where(x => x.Used > 0)
            .OrderByDescending(x => x.Used)
            .Take(take)
            .ToList();
    }

    private async Task<IReadOnlyList<AiQuotaStatus>> GetStatusCoreAsync(
        AiQuotaSettings current,
        int? userId,
        CancellationToken cancellationToken)
    {
        var (monthStart, resetAt) = CurrentMonth();
        var result = new List<AiQuotaStatus>();

        if (current.GlobalMonthlyTwd > 0 || userId is null)
        {
            var rows = await LoadRowsAsync(monthStart, userId: null, cancellationToken);
            result.Add(new AiQuotaStatus(AiQuotaScope.Global, current.GlobalMonthlyTwd, Sum(rows), monthStart, resetAt));
        }

        if (userId is { } id)
        {
            var rows = await LoadRowsAsync(monthStart, id, cancellationToken);
            result.Add(new AiQuotaStatus(AiQuotaScope.User, current.PerUserMonthlyTwd, Sum(rows), monthStart, resetAt));
        }

        return result;
    }

    private (DateTime MonthStart, DateTime ResetAt) CurrentMonth()
    {
        var now = timeProvider.GetLocalNow().DateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        return (monthStart, monthStart.AddMonths(1));
    }

    private async Task<List<QuotaRow>> LoadRowsAsync(DateTime monthStart, int? userId, CancellationToken cancellationToken)
    {
        var query = context.AiUsageLog.AsNoTracking().Where(x => x.OccurredAt >= monthStart);
        if (userId is { } id)
        {
            query = query.Where(x => x.UserId == id);
        }

        return await query
            .Select(x => new QuotaRow(x.UserId, x.UserName, x.OccurredAt, x.EstimatedCost, x.ExchangeRate))
            .ToListAsync(cancellationToken);
    }

    private decimal Sum(IEnumerable<QuotaRow> rows) => rows.Sum(x => ToTarget(x.EstimatedCost, x.ExchangeRate));

    /// <summary>
    /// 一列換算成目標幣別。沒金額（未定價）算 0；有金額沒匯率時用目前匯率，連目前匯率都沒有就算 0 並記警告。
    /// </summary>
    private decimal ToTarget(decimal? cost, decimal? rate)
    {
        if (cost is not { } amount || amount <= 0)
        {
            return 0m;
        }

        var converted = AiUsageExchange.ToTargetCurrency(amount, rate ?? exchangeRates.Current?.Rate);
        if (converted is null)
        {
            logger.LogWarning("AI quota cannot convert a usage row because no exchange rate is available; it counts as 0.");
            return 0m;
        }

        return converted.Value;
    }

    private sealed record QuotaRow(int? UserId, string? UserName, DateTime OccurredAt, decimal? EstimatedCost, decimal? ExchangeRate);
}
