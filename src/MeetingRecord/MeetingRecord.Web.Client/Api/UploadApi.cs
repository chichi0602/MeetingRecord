using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Uploads;

namespace MeetingRecord.Web.Client.Api;

/// <summary>上傳工作階段（1.1.23）：<c>api/v1/uploads</c>。分塊本身由 <c>chunk-upload.js</c> 直接送。</summary>
public sealed class UploadApi(ApiClient api)
{
    private const string BaseUrl = "api/v1/uploads";

    public Task<ApiResult<UploadSessionDto>> CreateAsync(UploadSessionRequestDto request)
        => api.PostAsync(BaseUrl, request, ClientJsonContext.Default.UploadSessionRequestDto, ClientJsonContext.Default.UploadSessionResult);

    /// <summary>專案附件（1.1.26）：先傳到伺服器的暫存檔，存專案時帶上傳編號。</summary>
    public Task<ApiResult<UploadSessionDto>> CreateProjectAttachmentAsync(UploadSessionRequestDto request)
        => api.PostAsync($"{BaseUrl}/project-attachments", request, ClientJsonContext.Default.UploadSessionRequestDto, ClientJsonContext.Default.UploadSessionResult);

    public Task<ApiResult<UploadStatusDto>> GetStatusAsync(Guid uploadId)
        => api.GetAsync($"{BaseUrl}/{uploadId}", ClientJsonContext.Default.UploadStatusResult);

    public Task<ApiResult<object>> CancelAsync(Guid uploadId)
        => api.DeleteAsync($"{BaseUrl}/{uploadId}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> FailAsync(Guid uploadId, string message)
        => api.PostAsync($"{BaseUrl}/{uploadId}/fail", new UploadFailRequestDto { Message = message }, ClientJsonContext.Default.UploadFailRequestDto, ClientJsonContext.Default.EmptyResult);
}
