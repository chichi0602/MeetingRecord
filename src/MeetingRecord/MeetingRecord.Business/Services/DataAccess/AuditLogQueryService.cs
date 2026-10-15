using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 稽核紀錄頁的查詢與清除（1.0.32，移植自 NET10-Blazor-Starter 的 AuditLogQueryService）。寫入在 <c>AuditLogService</c>。
///
/// <para>⚠️ <b>時間</b>：<see cref="AuditLog.OccurredAt"/> 存 UTC（全專案唯一存 UTC 的業務表，其他紀錄表存本地時間，
/// 不要照抄它們的清除或查詢寫法）。UTC↔本地的三個轉換點集中在這裡：篩選條件（本地→UTC）、
/// 結果（UTC→本地）、清除門檻（呼叫端傳 UTC）。SQLite 讀回來是 <see cref="DateTimeKind.Unspecified"/>，先標成 UTC 再轉。</para>
/// <para>注入 scoped <see cref="BackendDBContext"/>（本專案沒有 IDbContextFactory，同 <c>AiCallLogService</c>）；
/// 讀取一律 AsNoTracking、刪除用 ExecuteDelete，不碰變更追蹤。</para>
/// </summary>
public class AuditLogQueryService
{
    /// <summary>匯出 CSV 的筆數上限。</summary>
    public const int MaxExportRows = 10_000;

    private readonly BackendDBContext context;
    private readonly ILogger<AuditLogQueryService> logger;

    public AuditLogQueryService(BackendDBContext context, ILogger<AuditLogQueryService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    /// <summary>伺服器本地時間 → UTC（篩選條件用）。</summary>
    public static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>資料庫的 UTC → 伺服器本地時間（顯示用）。</summary>
    public static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    public async Task<DataRequestResult<AuditLogAdapterModel>> GetAsync(AuditLogQuery query)
    {
        var filtered = ApplyFilter(context.AuditLog.AsNoTracking(), query);

        // Skip/Take 之前一定要有 OrderBy；Id 當第二鍵，同一秒的多筆分頁時才不會重複或漏掉。
        IOrderedQueryable<AuditLog> sorted = query.SortDescending == false
            ? filtered.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            : filtered.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);

        var count = await sorted.CountAsync();
        var page = Math.Max(1, query.CurrentPage);
        var pageSize = Math.Max(1, query.PageSize);
        var rows = await sorted.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return new DataRequestResult<AuditLogAdapterModel> { Count = count, Result = rows.Select(ToModel).ToList() };
    }

    /// <summary>匯出用：同一組條件、不分頁，最多 <see cref="MaxExportRows"/> 筆（新到舊）。</summary>
    public async Task<IReadOnlyList<AuditLogAdapterModel>> GetForExportAsync(AuditLogQuery query)
    {
        var rows = await ApplyFilter(context.AuditLog.AsNoTracking(), query)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(MaxExportRows)
            .ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    /// <summary>
    /// 動作下拉：資料庫裡實際出現過的代碼。從資料取而不是從常數取——以後不再使用的舊代碼也篩得到。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDistinctActionsAsync()
        => await context.AuditLog.AsNoTracking()
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

    /// <summary>刪除 <paramref name="beforeUtc"/>（不含）之前的紀錄。</summary>
    /// <returns>刪除的列數。</returns>
    public Task<int> PurgeBeforeAsync(DateTime beforeUtc, CancellationToken cancellationToken = default)
        => context.AuditLog.Where(x => x.OccurredAt < beforeUtc).ExecuteDeleteAsync(cancellationToken);

    /// <summary>頁面的「清除 N 天前」：門檻以 UTC 計算。</summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days)
    {
        try
        {
            var removed = await PurgeBeforeAsync(DateTime.UtcNow.AddDays(-days));
            logger.LogInformation("Purged audit logs. Days={Days}, Rows={Rows}", days, removed);
            return VerifyRecordResultFactory.Build(true, removed == 0 ? "沒有符合條件的紀錄。" : $"已清除 {removed:N0} 筆紀錄。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge audit logs. Days={Days}", days);
            return VerifyRecordResultFactory.Build(false, "清除稽核紀錄失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        try
        {
            var removed = await context.AuditLog.ExecuteDeleteAsync();
            logger.LogInformation("Cleared all audit logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清除 {removed:N0} 筆紀錄。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear audit logs.");
            return VerifyRecordResultFactory.Build(false, "清空稽核紀錄失敗。", ex);
        }
    }

    private static IQueryable<AuditLog> ApplyFilter(IQueryable<AuditLog> source, AuditLogQuery query)
    {
        if (query.StartTime is { } start)
        {
            var startUtc = ToUtc(start);
            source = source.Where(x => x.OccurredAt >= startUtc);
        }

        if (query.EndTime is { } end)
        {
            var endUtc = ToUtc(end);
            source = source.Where(x => x.OccurredAt <= endUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            source = source.Where(x => x.Action == query.Action);
        }

        if (query.Success is { } success)
        {
            source = source.Where(x => x.Success == success);
        }

        if (!string.IsNullOrWhiteSpace(query.ActorAccount))
        {
            var account = query.ActorAccount.Trim();
            source = source.Where(x => x.ActorAccount != null && x.ActorAccount.Contains(account));
        }

        if (!string.IsNullOrWhiteSpace(query.ClientIp))
        {
            var ip = query.ClientIp.Trim();
            source = source.Where(x => x.ClientIp != null && x.ClientIp.StartsWith(ip));
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x =>
                x.Action.Contains(keyword)
                || (x.ActorAccount != null && x.ActorAccount.Contains(keyword))
                || (x.TargetType != null && x.TargetType.Contains(keyword))
                || (x.TargetId != null && x.TargetId.Contains(keyword))
                || (x.Detail != null && x.Detail.Contains(keyword))
                || (x.ClientIp != null && x.ClientIp.Contains(keyword)));
        }

        return source;
    }

    private static AuditLogAdapterModel ToModel(AuditLog x) => new()
    {
        Id = x.Id,
        OccurredAt = ToLocal(x.OccurredAt),
        OccurredAtUtc = DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc),
        ActorUserId = x.ActorUserId,
        ActorAccount = x.ActorAccount,
        Action = x.Action,
        TargetType = x.TargetType,
        TargetId = x.TargetId,
        Detail = x.Detail,
        Success = x.Success,
        ClientIp = x.ClientIp,
    };
}
