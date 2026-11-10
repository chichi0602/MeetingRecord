using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Jobs;

namespace MeetingRecord.Web.Client.Api;

/// <summary>背景工作進度 API（1.1.15）。</summary>
public sealed class JobApi(ApiClient api)
{
    public Task<ApiResult<List<BackgroundJobDto>>> GetAsync()
        => api.GetAsync("api/v1/jobs", ClientJsonContext.Default.BackgroundJobsResult);

    public Task<ApiResult<object>> CancelAsync(string kind, int meetingId)
        => api.PostAsync($"api/v1/jobs/{meetingId}/cancel?kind={Uri.EscapeDataString(kind)}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DismissAsync(string kind, int meetingId)
        => api.PostAsync($"api/v1/jobs/{meetingId}/dismiss?kind={Uri.EscapeDataString(kind)}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<int>> DismissAllAsync()
        => api.PostAsync("api/v1/jobs/dismiss-all", ClientJsonContext.Default.IntResult);
}
