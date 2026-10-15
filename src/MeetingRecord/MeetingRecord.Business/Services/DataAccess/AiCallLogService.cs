using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Other;
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
