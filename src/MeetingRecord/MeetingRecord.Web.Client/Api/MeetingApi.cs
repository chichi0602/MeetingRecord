using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;

namespace MeetingRecord.Web.Client.Api;

/// <summary>會議紀錄（1.1.23）：<c>api/v1/Meeting</c>。規則都在伺服器（MeetingService），這裡只轉呼叫。</summary>
public sealed class MeetingApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/Meeting";

    public Task<ApiResult<PagedResult<MeetingDto>>> ListAsync(MeetingListRequestDto request)
        => api.PostAsync($"{BaseUrl}/list", request, ClientJsonContext.Default.MeetingListRequestDto, ClientJsonContext.Default.MeetingPageResult);

    /// <summary>單筆（含會議紀錄全文；清單不帶全文）。</summary>
    public Task<ApiResult<MeetingDto>> GetAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.MeetingResult);

    public Task<ApiResult<MeetingDto>> CreateAsync(MeetingCreateUpdateDto meeting)
        => api.PostAsync(BaseUrl, meeting, ClientJsonContext.Default.MeetingCreateUpdateDto, ClientJsonContext.Default.MeetingResult);

    public Task<ApiResult<object>> UpdateAsync(MeetingCreateUpdateDto meeting)
        => api.PutAsync($"{BaseUrl}/{meeting.Id}", meeting, ClientJsonContext.Default.MeetingCreateUpdateDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{BaseUrl}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<int>> CountLinkedTodosAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}/linked-todos", ClientJsonContext.Default.IntResult);

    public Task<ApiResult<object>> RetranscribeAsync(int id)
        => api.PostAsync($"{BaseUrl}/{id}/retranscribe", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> RequestDraftAsync(int id, MeetingDraftRequestDto request)
        => api.PostAsync($"{BaseUrl}/{id}/draft", request, ClientJsonContext.Default.MeetingDraftRequestDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> UpdateDraftAsync(int id, string content)
        => api.PutAsync($"{BaseUrl}/{id}/draft", new MeetingDraftContentDto { Content = content }, ClientJsonContext.Default.MeetingDraftContentDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> AttachToProjectAsync(int id, int projectId)
        => api.PutAsync($"{BaseUrl}/{id}/project", new MeetingAttachRequestDto { ProjectId = projectId }, ClientJsonContext.Default.MeetingAttachRequestDto, ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<MeetingTranscriptDto>> GetTranscriptAsync(int id)
        => api.GetAsync($"{BaseUrl}/{id}/transcript", ClientJsonContext.Default.MeetingTranscriptResult);

    /// <summary>逐字稿 .txt（伺服器寫稽核）。</summary>
    public Task<ApiDownload> DownloadTranscriptAsync(int id)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{id}/transcript/file"));

    /// <summary>瀏覽器已複製逐字稿：請伺服器寫稽核。</summary>
    public Task<ApiResult<object>> TranscriptCopiedAsync(int id)
        => api.PostAsync($"{BaseUrl}/{id}/transcript/copied", ClientJsonContext.Default.EmptyResult);

    /// <summary>會議紀錄 PDF：帶 <paramref name="content"/> 就用它（編修中、還沒存的內容）。伺服器產生 PDF 並寫稽核。</summary>
    public Task<ApiDownload> DownloadDraftPdfAsync(int id, string? content)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{id}/draft/pdf")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new MeetingPdfRequestDto { Content = content }, ClientJsonContext.Default.MeetingPdfRequestDto),
        });

    public Task<ApiResult<List<MeetingProjectOptionDto>>> GetSelectableProjectsAsync()
        => api.GetAsync($"{BaseUrl}/projects/selectable", ClientJsonContext.Default.MeetingProjectOptionsResult);

    public Task<ApiResult<List<MeetingPromptOptionDto>>> GetSelectablePromptTemplatesAsync()
        => api.GetAsync($"{BaseUrl}/prompt-templates/selectable", ClientJsonContext.Default.MeetingPromptOptionsResult);

    public Task<ApiResult<List<string>>> GetEnabledCategoriesAsync()
        => api.GetAsync($"{BaseUrl}/categories/enabled", ClientJsonContext.Default.StringListResult);
}
