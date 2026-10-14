using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 系統例外紀錄的資料存取。
///
/// ⚠️ <b>本服務位於例外記錄管線之內。</b><see cref="RecordAsync"/> 由背景寫入器呼叫，
/// 它自己的失敗絕不可再經由 <see cref="ILogger"/> 回報，否則會形成
/// 「寫入失敗 → 記錯誤 → 再寫入 → 再失敗」的無限遞迴。查詢與刪除路徑由畫面呼叫，
/// 不在管線內，可以正常記錄。
///
/// 0.4.122 移植自 NET10-Blazor-Starter。Starter 用 <c>IDbContextFactory</c>；本專案沒有註冊，
/// 改注入 scoped <see cref="BackendDBContext"/>——背景寫入器每筆例外自建一個 scope，
/// 畫面端由 View 的早退避免同一個 circuit 的 DbContext 被並行使用。
/// </summary>
public class ExceptionLogService
{
    /// <summary>
    /// 相異例外的列數上限。帶參數的例外訊息（例如「no such column: X」）可能衝出大量不同列，
    /// 達上限後改記哨兵列，資料庫就不會被塞爆。
    /// </summary>
    public const int MaxRows = 5000;

    private readonly BackendDBContext context;
    private readonly ExceptionStackFileStore fileStore;

    public IMapper Mapper { get; }
    public ILogger<ExceptionLogService> Logger { get; }

    public ExceptionLogService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<ExceptionLogService> logger,
        ExceptionStackFileStore fileStore)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.fileStore = fileStore;
    }

    /// <summary>
    /// 記錄一筆例外：相同簽章就累加次數，否則新增一列並寫入堆疊檔。
    /// 回傳寫入後的狀態；記錄失敗時回傳 null（寫入器據此計入寫入失敗）。
    ///
    /// ⚠️ 由背景寫入器呼叫，全程不得拋出、不得走 ILogger（見類別註解）。
    /// </summary>
    public async Task<ExceptionRecordOutcome?> RecordAsync(ExceptionLogEntry entry)
    {
        try
        {
            return await RecordCoreAsync(entry);
        }
        catch (Exception)
        {
            // 刻意吞掉。例外記錄本身失敗不該影響系統，更不該回流管線造成遞迴。
            return null;
        }
    }

    private static ExceptionRecordOutcome ToOutcome(ExceptionLog row, bool isNew, ExceptionLogEntry entry)
        => new(
            row.Id,
            isNew,
            row.Signature == ExceptionSignature.OverflowSignature,
            row.ExceptionType,
            row.Source,
            row.Page,
            row.OccurrenceCount,
            row.FirstOccurredAt,
            row.LastOccurredAt,
            entry.TraceId,
            entry.IsCritical);

    private async Task<ExceptionRecordOutcome?> RecordCoreAsync(ExceptionLogEntry entry)
    {
        var message = ExceptionSignature.TruncateMessage(entry.Message);
        var signature = ExceptionSignature.Compute(entry.ExceptionType, message, entry.Page, entry.Operation);

        var existing = await context.ExceptionLog.FirstOrDefaultAsync(x => x.Signature == signature);
        if (existing is not null)
        {
            existing.OccurrenceCount++;
            existing.LastOccurredAt = entry.OccurredAt;
            existing.LastTraceId = entry.TraceId;
            await context.SaveChangesAsync();
            return ToOutcome(existing, isNew: false, entry);
        }

        // 達列數上限：不再新增相異列，改累加哨兵列。
        if (await context.ExceptionLog.CountAsync() >= MaxRows)
        {
            var overflow = await RecordOverflowAsync(context, entry.OccurredAt);
            return ToOutcome(overflow, isNew: false, entry);
        }

        // 堆疊檔在建立資料列之前寫，失敗只是少了全文，資料列仍必須建立。
        var stackFile = await fileStore.WriteAsync(signature, entry.OccurredAt, entry.StackTrace);

        var item = new ExceptionLog
        {
            Signature = signature,
            ExceptionType = entry.ExceptionType,
            Message = message,
            Source = entry.Source,
            Page = entry.Page,
            Operation = entry.Operation,
            LoggerName = entry.LoggerName,
            Account = entry.Account,
            UserId = entry.UserId,
            StackTraceFile = stackFile,
            OccurrenceCount = 1,
            FirstOccurredAt = entry.OccurredAt,
            LastOccurredAt = entry.OccurredAt,
            LastTraceId = entry.TraceId,
        };

        try
        {
            await context.ExceptionLog.AddAsync(item);
            await context.SaveChangesAsync();
            return ToOutcome(item, isNew: true, entry);
        }
        catch (Exception)
        {
            // 資料列沒建起來，剛才那個堆疊檔就是孤兒 —— 一律清掉。
            // ⚠️ 刻意攔所有例外而非只攔 DbUpdateException：任何原因造成資料列沒進去，
            // 檔案都必須一起收乾淨，否則目錄會慢慢累積對不到紀錄的孤兒檔。
            fileStore.Delete(stackFile);

            // 丟掉加不進去的那一列，再看是不是被別人搶先建了同一個簽章。
            context.ChangeTracker.Clear();
            var conflicting = await context.ExceptionLog.FirstOrDefaultAsync(x => x.Signature == signature);
            if (conflicting is not null)
            {
                conflicting.OccurrenceCount++;
                conflicting.LastOccurredAt = entry.OccurredAt;
                conflicting.LastTraceId = entry.TraceId;
                await context.SaveChangesAsync();
                return ToOutcome(conflicting, isNew: false, entry);
            }

            return null;
        }
    }

    private static async Task<ExceptionLog> RecordOverflowAsync(BackendDBContext context, DateTime occurredAt)
    {
        var overflow = await context.ExceptionLog
            .FirstOrDefaultAsync(x => x.Signature == ExceptionSignature.OverflowSignature);

        if (overflow is null)
        {
            overflow = new ExceptionLog
            {
                Signature = ExceptionSignature.OverflowSignature,
                ExceptionType = "(其他)",
                Message = $"相異例外已達列數上限（{MaxRows}），之後的新例外一律併入本列。請清除舊紀錄後再觀察。",
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = occurredAt,
                LastOccurredAt = occurredAt,
            };
            await context.ExceptionLog.AddAsync(overflow);
        }
        else
        {
            overflow.OccurrenceCount++;
            overflow.LastOccurredAt = occurredAt;
        }

        await context.SaveChangesAsync();
        return overflow;
    }

    public async Task<DataRequestResult<ExceptionLogAdapterModel>> GetAsync(ExceptionLogQuery query)
    {
        Logger.LogDebug(
            "Loading exception logs. Source={Source}, CurrentPage={CurrentPage}, PageSize={PageSize}",
            query.Source,
            query.CurrentPage,
            query.PageSize);

        DataRequestResult<ExceptionLogAdapterModel> result = new();
        IQueryable<ExceptionLog> dataSource = context.ExceptionLog.AsNoTracking();

        if (query.StartTime.HasValue)
        {
            dataSource = dataSource.Where(x => x.LastOccurredAt >= query.StartTime.Value);
        }

        if (query.EndTime.HasValue)
        {
            dataSource = dataSource.Where(x => x.LastOccurredAt <= query.EndTime.Value);
        }

        if (string.IsNullOrWhiteSpace(query.Source) == false)
        {
            dataSource = dataSource.Where(x => x.Source == query.Source);
        }

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            dataSource = dataSource.Where(x => x.Account != null && x.Account.Contains(query.Account));
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            dataSource = dataSource.Where(x =>
                x.ExceptionType.Contains(query.Keyword) ||
                x.Message.Contains(query.Keyword) ||
                (x.Page != null && x.Page.Contains(query.Keyword)) ||
                (x.Operation != null && x.Operation.Contains(query.Keyword)) ||
                (x.LastTraceId != null && x.LastTraceId == query.Keyword.Trim().ToUpperInvariant()));
        }

        IOrderedQueryable<ExceptionLog>? sorted = null;

        if (string.IsNullOrWhiteSpace(query.SortField) == false)
        {
            if (query.SortField == nameof(ExceptionLogAdapterModel.OccurrenceCount))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.OccurrenceCount).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.OccurrenceCount).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(ExceptionLogAdapterModel.LastOccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.LastOccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.LastOccurredAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(ExceptionLogAdapterModel.FirstOccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.FirstOccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.FirstOccurredAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 之前一定要有 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.LastOccurredAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource
            .Skip((query.CurrentPage - 1) * query.PageSize)
            .Take(query.PageSize);

        List<ExceptionLog> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<ExceptionLogAdapterModel>>(records);
        return result;
    }

    /// <summary>讀取堆疊全文。檔案不存在時回 null，由畫面顯示友善訊息。</summary>
    public async Task<string?> GetStackTraceAsync(int id)
    {
        var item = await context.ExceptionLog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        return item is null ? null : await fileStore.ReadAsync(item.StackTraceFile);
    }

    /// <summary>
    /// 一次讀取多筆的堆疊全文（「複製目前查詢結果」用）。每個傳入的 id 都有一個鍵；
    /// 找不到資料列或堆疊檔不存在時值為 null。
    /// 刻意只查一次資料庫：整次查詢最多 <see cref="MaxRows"/> 列，逐筆呼叫 <see cref="GetStackTraceAsync"/> 會開同樣多個 DbContext。
    /// </summary>
    public async Task<Dictionary<int, string?>> GetStackTracesAsync(IReadOnlyCollection<int> ids)
    {
        var files = await context.ExceptionLog
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.StackTraceFile);

        var result = new Dictionary<int, string?>(ids.Count);
        foreach (var id in ids)
        {
            result[id] = files.TryGetValue(id, out var file) ? await fileStore.ReadAsync(file) : null;
        }

        return result;
    }

    /// <summary>刪除單一列，同時刪除其堆疊檔。</summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting exception log. ExceptionLogId={ExceptionLogId}", id);

        try
        {
            var item = await context.ExceptionLog.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的例外紀錄。");
            }

            var stackFile = item.StackTraceFile;
            context.ExceptionLog.Remove(item);
            await context.SaveChangesAsync();

            fileStore.Delete(stackFile);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete exception log. ExceptionLogId={ExceptionLogId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除例外紀錄失敗。", ex);
        }
    }

    /// <summary>清空全部紀錄與整個堆疊目錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        Logger.LogInformation("Clearing all exception logs.");

        try
        {
            var removed = await context.ExceptionLog.ExecuteDeleteAsync();
            fileStore.DeleteAll();

            Logger.LogInformation("Cleared all exception logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear exception logs.");
            return VerifyRecordResultFactory.Build(false, "清空例外紀錄失敗。", ex);
        }
    }

    /// <summary>刪除「最後發生」早於 N 天前的紀錄，同時刪除其堆疊檔。</summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days)
    {
        Logger.LogInformation("Purging exception logs older than {Days} days.", days);

        try
        {
            var removed = await PurgeBeforeAsync(DateTime.Now.AddDays(-days));
            if (removed == 0)
            {
                return VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。");
            }

            Logger.LogInformation("Purged exception logs. Rows={Rows}, Days={Days}", removed, days);
            return VerifyRecordResultFactory.Build(true, $"已清除 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge exception logs. Days={Days}", days);
            return VerifyRecordResultFactory.Build(false, "清除舊例外紀錄失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除「最後發生」早於 <paramref name="threshold"/>（本地時間，與 LastOccurredAt 同一個時間基準）的紀錄與堆疊檔，
    /// 回傳刪除筆數。供頁面手動清除與自動保留期限（ExceptionLogRetentionBackgroundService）共用；失敗時拋出，由呼叫端決定如何回報。
    /// </summary>
    public async Task<int> PurgeBeforeAsync(DateTime threshold)
    {

        var stale = await context.ExceptionLog
            .Where(x => x.LastOccurredAt < threshold)
            .ToListAsync();

        if (stale.Count == 0)
        {
            return 0;
        }

        // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
        var stackFiles = stale.Select(x => x.StackTraceFile).ToList();
        context.ExceptionLog.RemoveRange(stale);
        await context.SaveChangesAsync();

        foreach (var stackFile in stackFiles)
        {
            fileStore.Delete(stackFile);
        }

        return stale.Count;
    }
}
