using System.Net.Http.Json;
using MeetingRecord.Dtos.AiUsage;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>Token 用量（1.1.37）：<c>api/v1/ai-usage</c>（1.1.36），只限管理者。匯出、刪除、清除的稽核由伺服器寫。</summary>
public sealed class AiUsageApi(ApiClient api)
{
    private const string Base = "api/v1/ai-usage";

    public Task<ApiResult<AiUsageOverviewDto>> GetOverviewAsync()
        => api.GetAsync($"{Base}/overview", ClientJsonContext.Default.AiUsageOverviewResult);

    public Task<ApiResult<AiUsageFilterOptions>> GetFilterOptionsAsync()
        => api.GetAsync($"{Base}/filter-options", ClientJsonContext.Default.AiUsageFilterOptionsResult);

    public Task<ApiResult<AiUsageReport>> GetReportAsync(AiUsageFilter filter)
        => api.PostAsync($"{Base}/report", filter, ClientJsonContext.Default.AiUsageFilter, ClientJsonContext.Default.AiUsageReportResult);

    public Task<ApiResult<AiUsagePagedResult>> GetRowsAsync(AiUsageQuery query)
        => api.PostAsync($"{Base}/rows", query, ClientJsonContext.Default.AiUsageQuery, ClientJsonContext.Default.AiUsageRowsResult);

    public Task<ApiResult<AiUsageDetailResponseDto>> GetDetailAsync(int id)
        => api.GetAsync($"{Base}/{id}", ClientJsonContext.Default.AiUsageDetailResult);

    public Task<ApiDownload> ExportCsvAsync(AiUsageFilter filter)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/export/csv")
        {
            Content = JsonContent.Create(filter, ClientJsonContext.Default.AiUsageFilter),
        });

    public Task<ApiDownload> ExportPdfAsync(AiUsageFilter filter)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/export/pdf")
        {
            Content = JsonContent.Create(filter, ClientJsonContext.Default.AiUsageFilter),
        });

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{Base}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<int>> PurgeAsync(DateTime before)
        => api.PostAsync($"{Base}/purge", new AiUsagePurgeRequestDto { Before = before },
            ClientJsonContext.Default.AiUsagePurgeRequestDto, ClientJsonContext.Default.IntResult);

    public Task<ApiResult<int>> ClearAllAsync()
        => api.PostAsync($"{Base}/clear-all", ClientJsonContext.Default.IntResult);
}
