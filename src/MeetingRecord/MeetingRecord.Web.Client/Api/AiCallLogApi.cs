using System.Net.Http.Json;
using MeetingRecord.Dtos.AiCallLogs;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>AI 對話紀錄（1.1.39）：<c>api/v1/ai-call-logs</c>（1.1.38），只限管理者。查看、下載、匯出、刪除、清除的稽核由伺服器寫。</summary>
public sealed class AiCallLogApi(ApiClient api)
{
    private const string Base = "api/v1/ai-call-logs";

    public Task<ApiResult<AiCallLogMetaDto>> GetMetaAsync()
        => api.GetAsync($"{Base}/meta", ClientJsonContext.Default.AiCallLogMetaResult);

    public Task<ApiResult<AiCallLogFilterOptions>> GetFilterOptionsAsync()
        => api.GetAsync($"{Base}/filter-options", ClientJsonContext.Default.AiCallLogFilterOptionsResult);

    public Task<ApiResult<PagedResult<AiCallLogAdapterModel>>> SearchAsync(AiCallLogQuery query)
        => api.PostAsync($"{Base}/search", query, ClientJsonContext.Default.AiCallLogQuery, ClientJsonContext.Default.AiCallLogPageResult);

    public Task<ApiResult<int>> FindIdByCallIdAsync(Guid callId)
        => api.GetAsync($"{Base}/by-call-id/{callId:D}", ClientJsonContext.Default.IntResult);

    public Task<ApiResult<AiCallLogDetail>> GetDetailAsync(int id)
        => api.GetAsync($"{Base}/{id}", ClientJsonContext.Default.AiCallLogDetailResult);

    public Task<ApiDownload> DownloadJsonAsync(int id)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, $"{Base}/{id}/json"));

    public Task<ApiDownload> ExportPdfAsync(int id)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/pdf"));

    public Task<ApiDownload> ExportCsvAsync(AiCallLogQuery query)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/export/csv")
        {
            Content = JsonContent.Create(query, ClientJsonContext.Default.AiCallLogQuery),
        });

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{Base}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<int>> PurgeAsync(DateTime before)
        => api.PostAsync($"{Base}/purge", new AiCallLogPurgeRequestDto { Before = before },
            ClientJsonContext.Default.AiCallLogPurgeRequestDto, ClientJsonContext.Default.IntResult);

    public Task<ApiResult<object>> ClearAllAsync()
        => api.PostAsync($"{Base}/clear-all", ClientJsonContext.Default.EmptyResult);
}
