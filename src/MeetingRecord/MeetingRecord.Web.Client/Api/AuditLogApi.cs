using System.Net.Http.Json;
using MeetingRecord.Dtos.AuditLogs;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>稽核紀錄（1.1.43）：<c>api/v1/audit-logs</c>，只限管理者。匯出與清除的稽核由伺服器寫（清除類在刪除之後）。</summary>
public sealed class AuditLogApi(ApiClient api)
{
    private const string Base = "api/v1/audit-logs";

    public Task<ApiResult<AuditLogMetaDto>> GetMetaAsync()
        => api.GetAsync($"{Base}/meta", ClientJsonContext.Default.AuditLogMetaResult);

    public Task<ApiResult<List<string>>> GetActionsAsync()
        => api.GetAsync($"{Base}/actions", ClientJsonContext.Default.StringListResult);

    public Task<ApiResult<PagedResult<AuditLogAdapterModel>>> SearchAsync(AuditLogQuery query)
        => api.PostAsync($"{Base}/search", query, ClientJsonContext.Default.AuditLogQuery, ClientJsonContext.Default.AuditLogPageResult);

    public Task<ApiDownload> ExportCsvAsync(AuditLogQuery query)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/export/csv")
        {
            Content = JsonContent.Create(query, ClientJsonContext.Default.AuditLogQuery),
        });

    public Task<ApiResult<object>> PurgeAsync()
        => api.PostAsync($"{Base}/purge", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> ClearAllAsync()
        => api.PostAsync($"{Base}/clear-all", ClientJsonContext.Default.EmptyResult);
}
