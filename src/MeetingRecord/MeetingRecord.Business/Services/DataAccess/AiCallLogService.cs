using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>寫入 AI 對話紀錄的介面（<c>AiCallGateway</c> 用；測試可替換）。</summary>
public interface IAiCallLogRecorder
{
    /// <summary>目前是否記錄（<c>AiCallLogSettings:Enabled</c>）。</summary>
    bool IsEnabled { get; }

    /// <summary>寫一筆對話紀錄。⚠️ <b>永遠不拋例外</b>：記錄失敗不可以影響 AI 呼叫本身。</summary>
    Task RecordAsync(AiCallLogEntry entry);
}

/// <summary>
/// AI 對話紀錄的資料存取（1.0.29，移植自 NET10-Blazor-Starter 的 AiCallLogService）。
///
/// <para>
/// 一列中繼資料存在 <see cref="AiCallLog"/>，送出內容與回應全文存成 JSON 檔（<see cref="AiCallLogFileStore"/>）。
/// 檔案先寫、列後寫；列寫不進去就刪檔，不留孤兒。
/// </para>
///
/// <para>
/// 注入 scoped <see cref="BackendDBContext"/>（本專案沒有 IDbContextFactory，同 <c>ExceptionLogService</c>）。
/// ⚠️ 由 <c>AiCallGateway</c> 在呼叫端修改自己的實體<b>之前</b>呼叫（與 <c>AiUsageRecorder</c> 同一個規則），
/// 寫入失敗時把自己加的列從追蹤中移除。
/// </para>
/// </summary>
public class AiCallLogService : IAiCallLogRecorder
{
    private const int RelatedInfoMaxLength = 500;
    private const int ThreadKeyMaxLength = 100;
    private const int ErrorMessageMaxLength = 500;

    /// <summary>匯出 CSV 的筆數上限。</summary>
    public const int MaxExportRows = 10_000;

    /// <summary>內容檔的 JSON 設定：中文不跳脫（檔案是給人看的），縮排方便直接打開閱讀。</summary>
    internal static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly BackendDBContext context;
    private readonly AiCallLogFileStore fileStore;
    private readonly IOptionsMonitor<AiCallLogSettings> settings;
    private readonly ILogger<AiCallLogService> logger;

    public AiCallLogService(
        BackendDBContext context,
        AiCallLogFileStore fileStore,
        IOptionsMonitor<AiCallLogSettings> settings,
        ILogger<AiCallLogService> logger)
    {
        this.context = context;
        this.fileStore = fileStore;
        this.settings = settings;
        this.logger = logger;
    }

    public bool IsEnabled => settings.CurrentValue.Enabled;

    public int RetentionDays => settings.CurrentValue.RetentionDays;

    public async Task RecordAsync(AiCallLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsEnabled)
        {
            return;
        }

        string? contentFile = null;
        AiCallLog? row = null;
        try
        {
            contentFile = await fileStore.WriteAsync(
                entry.OccurredAt,
                entry.CallId,
                JsonSerializer.Serialize(entry, FileJsonOptions));

            row = new AiCallLog
            {
                CallId = entry.CallId,
                OccurredAt = entry.OccurredAt,
                Feature = entry.Feature,
                Outcome = entry.Outcome,
                Provider = entry.Provider ?? string.Empty,
                Model = entry.Model ?? string.Empty,
                UserId = entry.UserId,
                UserName = entry.UserName,
                MeetingId = entry.MeetingId,
                ProjectId = entry.ProjectId,
                RelatedInfo = Truncate(entry.RelatedInfo, RelatedInfoMaxLength),
                ThreadKey = Truncate(entry.ThreadKey, ThreadKeyMaxLength),
                HttpStatus = entry.HttpStatus,
                ElapsedMilliseconds = entry.ElapsedMilliseconds,
                RequestCharacters = entry.Request.Messages.Sum(x => x.Content?.Length ?? 0),
                ResponseCharacters = entry.Response.Text?.Length ?? 0,
                IsResponsePartial = entry.Response.IsPartial,
                ErrorMessage = Truncate(entry.ErrorMessage, ErrorMessageMaxLength),
                ContentFile = contentFile,
            };

            context.AiCallLog.Add(row);
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (row is not null)
            {
                context.Entry(row).State = EntityState.Detached;
            }

            // 列沒建起來，檔案就沒有人指向它——刪掉，不留孤兒。
            fileStore.Delete(contentFile);

            logger.LogError(
                ex,
                "Failed to write AI call log. Feature={Feature}, Outcome={Outcome}, CallId={CallId}",
                entry.Feature,
                entry.Outcome,
                entry.CallId);
        }
    }

    #region 查詢（1.0.30：AI 對話紀錄頁）

    public async Task<DataRequestResult<AiCallLogAdapterModel>> GetAsync(AiCallLogQuery query)
    {
        var filtered = ApplyFilter(context.AiCallLog.AsNoTracking(), query);

        // Skip/Take 之前一定要有 OrderBy；欄位不認得或方向為 null 一律退回預設（新到舊）。
        IOrderedQueryable<AiCallLog> sorted = (query.SortField, query.SortDescending) switch
        {
            (nameof(AiCallLogAdapterModel.OccurredAt), false) => filtered.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id),
            (nameof(AiCallLogAdapterModel.ElapsedMilliseconds), true) => filtered.OrderByDescending(x => x.ElapsedMilliseconds).ThenByDescending(x => x.Id),
            (nameof(AiCallLogAdapterModel.ElapsedMilliseconds), false) => filtered.OrderBy(x => x.ElapsedMilliseconds).ThenBy(x => x.Id),
            (nameof(AiCallLogAdapterModel.RequestCharacters), true) => filtered.OrderByDescending(x => x.RequestCharacters).ThenByDescending(x => x.Id),
            (nameof(AiCallLogAdapterModel.RequestCharacters), false) => filtered.OrderBy(x => x.RequestCharacters).ThenBy(x => x.Id),
            _ => filtered.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id),
        };

        var count = await sorted.CountAsync();
        var page = Math.Max(1, query.CurrentPage);
        var rows = await sorted.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        return new DataRequestResult<AiCallLogAdapterModel> { Count = count, Result = rows.Select(ToModel).ToList() };
    }

    /// <summary>匯出用：同一組條件、不分頁，最多 <see cref="MaxExportRows"/> 筆（新到舊）。</summary>
    public async Task<IReadOnlyList<AiCallLogAdapterModel>> GetForExportAsync(AiCallLogQuery query)
    {
        var rows = await ApplyFilter(context.AiCallLog.AsNoTracking(), query)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(MaxExportRows)
            .ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    /// <summary>篩選下拉：資料庫裡實際出現過的使用者與模型。</summary>
    public async Task<AiCallLogFilterOptions> GetFilterOptionsAsync()
    {
        var users = await context.AiCallLog.AsNoTracking()
            .Where(x => x.UserId != null)
            .Select(x => new { x.UserId, x.UserName, x.OccurredAt })
            .ToListAsync();
        var models = await context.AiCallLog.AsNoTracking()
            .Select(x => x.Model)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        return new AiCallLogFilterOptions(
            users
                .GroupBy(x => x.UserId!.Value)
                .Select(g => (g.Key, g.OrderByDescending(x => x.OccurredAt).Select(x => x.UserName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? $"#{g.Key}"))
                .OrderBy(x => x.Item2, StringComparer.CurrentCulture)
                .ToList(),
            models.Where(x => !string.IsNullOrWhiteSpace(x)).ToList());
    }

    /// <summary>從 Token 用量跳過來（<c>/ai-call-logs?callId=</c>）時找出那一筆。</summary>
    public Task<int?> FindIdByCallIdAsync(Guid callId)
        => context.AiCallLog.AsNoTracking().Where(x => x.CallId == callId).Select(x => (int?)x.Id).FirstOrDefaultAsync();

    /// <summary>明細：列、內容檔、同一次呼叫的用量、同一趟工作的其他呼叫。找不到回 null。</summary>
    public async Task<AiCallLogDetail?> GetDetailAsync(int id)
    {
        var row = await context.AiCallLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (row is null)
        {
            return null;
        }

        var rawJson = await fileStore.ReadAsync(row.ContentFile);
        AiCallLogEntry? content = null;
        if (rawJson is not null)
        {
            try
            {
                content = JsonSerializer.Deserialize<AiCallLogEntry>(rawJson, FileJsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "AI call log content file is not valid JSON. Id={Id}", id);
            }
        }

        var usage = await context.AiUsageLog.AsNoTracking()
            .Where(x => x.CallId == row.CallId)
            .Select(x => new AiCallLogUsage(
                x.InputTokens, x.OutputTokens, x.CachedInputTokens, x.AudioSeconds, x.IsAudioDurationEstimated,
                x.EstimatedCost, x.Currency, x.ExchangeRate, x.ConvertedCurrency))
            .FirstOrDefaultAsync();

        List<AiCallLog> thread = string.IsNullOrWhiteSpace(row.ThreadKey)
            ? [row]
            : await context.AiCallLog.AsNoTracking()
                .Where(x => x.ThreadKey == row.ThreadKey)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                .ToListAsync();

        return new AiCallLogDetail(ToModel(row), content, rawJson, usage, thread.Select(ToModel).ToList());
    }

    #endregion

    #region 刪除（1.0.30：頁面的刪除、清除此日之前、全部清除）

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        try
        {
            var removed = await DeleteWhereAsync(context.AiCallLog.Where(x => x.Id == id), CancellationToken.None);
            return removed == 0
                ? VerifyRecordResultFactory.Build(false, "找不到要刪除的 AI 對話紀錄。")
                : VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete AI call log. Id={Id}", id);
            return VerifyRecordResultFactory.Build(false, "刪除 AI 對話紀錄失敗。", ex);
        }
    }

    /// <summary>刪除 <paramref name="before"/>（不含）之前的紀錄與內容檔。</summary>
    /// <returns>刪除的列數。</returns>
    public async Task<int> PurgeBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        var removed = await DeleteWhereAsync(context.AiCallLog.Where(x => x.OccurredAt < before), cancellationToken);
        fileStore.DeleteMonthsBefore(before);
        return removed;
    }

    /// <summary>清空全部紀錄與整個內容檔目錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        try
        {
            var removed = await context.AiCallLog.ExecuteDeleteAsync();
            fileStore.DeleteAll();
            logger.LogInformation("Cleared all AI call logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清除 {removed:N0} 筆。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear AI call logs.");
            return VerifyRecordResultFactory.Build(false, "清空 AI 對話紀錄失敗。", ex);
        }
    }

    #endregion

    private static IQueryable<AiCallLog> ApplyFilter(IQueryable<AiCallLog> source, AiCallLogQuery query)
    {
        if (query.StartTime is { } start)
        {
            source = source.Where(x => x.OccurredAt >= start);
        }

        if (query.EndTime is { } end)
        {
            source = source.Where(x => x.OccurredAt <= end);
        }

        if (query.Feature is { } feature)
        {
            source = source.Where(x => x.Feature == feature);
        }

        if (query.Outcome is { } outcome)
        {
            source = source.Where(x => x.Outcome == outcome);
        }

        if (query.UserId is { } userId)
        {
            source = source.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            source = source.Where(x => x.Model == query.Model);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = Guid.TryParse(keyword, out var callId)
                ? source.Where(x => x.CallId == callId)
                : source.Where(x => x.RelatedInfo != null && x.RelatedInfo.Contains(keyword));
        }

        return source;
    }

    private static AiCallLogAdapterModel ToModel(AiCallLog x) => new()
    {
        Id = x.Id,
        CallId = x.CallId,
        OccurredAt = x.OccurredAt,
        Feature = x.Feature,
        Outcome = x.Outcome,
        Provider = x.Provider,
        Model = x.Model,
        UserId = x.UserId,
        UserName = x.UserName,
        MeetingId = x.MeetingId,
        ProjectId = x.ProjectId,
        RelatedInfo = x.RelatedInfo,
        ThreadKey = x.ThreadKey,
        HttpStatus = x.HttpStatus,
        ElapsedMilliseconds = x.ElapsedMilliseconds,
        RequestCharacters = x.RequestCharacters,
        ResponseCharacters = x.ResponseCharacters,
        IsResponsePartial = x.IsResponsePartial,
        ErrorMessage = x.ErrorMessage,
        ContentFile = x.ContentFile,
    };

    /// <summary>
    /// 刪除某場會議的全部對話紀錄（連同內容檔）。刪除會議時呼叫；用量帳本不動。
    /// </summary>
    /// <returns>刪除的列數。</returns>
    public Task<int> DeleteByMeetingAsync(int meetingId, CancellationToken cancellationToken = default)
        => DeleteWhereAsync(context.AiCallLog.Where(x => x.MeetingId == meetingId), cancellationToken);

    /// <summary>
    /// 刪除某個專案<b>本身</b>的對話紀錄（專案層級的 AI 問答，也就是沒有 MeetingId 的列）。
    /// 會議在專案刪除後仍然存在（外鍵是 SetNull），會議的紀錄跟著會議走，所以這裡不刪。
    /// </summary>
    public Task<int> DeleteByProjectAsync(int projectId, CancellationToken cancellationToken = default)
        => DeleteWhereAsync(context.AiCallLog.Where(x => x.ProjectId == projectId && x.MeetingId == null), cancellationToken);

    /// <summary>
    /// 清除超過保留天數的紀錄（連同內容檔），並刪掉更早的整個月份目錄（收尾孤兒檔）。
    /// 停用記錄時照樣清除——舊紀錄不該因為停用而永久留下。
    /// </summary>
    /// <returns>刪除的列數。</returns>
    public async Task<int> PurgeExpiredAsync(DateTime nowLocal, CancellationToken cancellationToken = default)
    {
        var threshold = nowLocal.AddDays(-RetentionDays);
        var removed = await DeleteWhereAsync(context.AiCallLog.Where(x => x.OccurredAt < threshold), cancellationToken);
        fileStore.DeleteMonthsBefore(threshold);
        return removed;
    }

    /// <summary>
    /// 先抓出要刪的內容檔，再用 <c>ExecuteDelete</c> 刪列（不經變更追蹤，不會連帶送出呼叫端的其他變更），最後刪檔。
    /// </summary>
    private async Task<int> DeleteWhereAsync(IQueryable<AiCallLog> query, CancellationToken cancellationToken)
    {
        var files = await query.Where(x => x.ContentFile != null).Select(x => x.ContentFile).ToListAsync(cancellationToken);
        var removed = await query.ExecuteDeleteAsync(cancellationToken);

        foreach (var file in files)
        {
            fileStore.Delete(file);
        }

        return removed;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
