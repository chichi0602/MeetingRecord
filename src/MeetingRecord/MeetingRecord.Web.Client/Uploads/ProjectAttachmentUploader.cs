using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Web.Client.Uploads;

/// <summary>
/// 專案頁的附件上傳（1.1.26）：選好檔案就開始傳、等傳完才列進「待上傳」，按存檔時把上傳編號交給
/// <c>POST／PUT api/v1/Project/…/form</c>。位元組由 <c>chunk-upload.js</c> 分塊送，伺服器先寫到暫存檔（1.1.24）。
///
/// <para>
/// 與會議影音檔不同，這裡**等上傳結束才回來**（表單上顯示讀取中），不是背景上傳：附件是表單內容的一部分，
/// 傳完才知道能不能存。瀏覽器送不下去時回報 <c>fail</c>，伺服器立刻結束、原因帶回畫面。
/// </para>
/// </summary>
public sealed class ProjectAttachmentUploader(UploadApi api, IJSRuntime js, ILogger<ProjectAttachmentUploader> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 上傳 <paramref name="input"/> 裡第 <paramref name="fileIndex"/> 個檔案。成功回上傳編號；失敗回原因
    /// （例如「{檔名} 上傳失敗：…」「{檔名} 讀取失敗，請重新選擇。」）。
    /// </summary>
    public async Task<(Guid? UploadId, string? Error)> UploadAsync(ElementReference input, int fileIndex, IBrowserFile file)
    {
        var created = await api.CreateProjectAttachmentAsync(new UploadSessionRequestDto
        {
            Purpose = UploadPurposes.ProjectAttachment,
            FileName = file.Name,
            ContentType = file.ContentType,
            Size = file.Size,
        });
        if (!created.Success || created.Data is not { } session)
        {
            return (null, ApiClient.ErrorText(created));
        }

        using var callback = DotNetObjectReference.Create(new AttachmentChunkCallback(api, session.UploadId, logger));
        try
        {
            await js.InvokeVoidAsync("meetingRecordChunkUpload.start", input, fileIndex, file.Name, file.Size, session.UploadId.ToString(), session.ChunkSize, callback);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Failed to start project attachment upload. UploadId={UploadId}", session.UploadId);
            await api.CancelAsync(session.UploadId);
            return (null, $"{file.Name} 讀取失敗，請重新選擇。");
        }

        while (true)
        {
            await Task.Delay(PollInterval);
            var status = await api.GetStatusAsync(session.UploadId);
            if (status.StatusCode == 401)
            {
                return (null, "登入已失效，請重新登入後再上傳。"); // ApiClient 已導到登入頁。
            }

            if (status.Data is { } current && current.State != UploadStates.Uploading)
            {
                return current.State == UploadStates.Succeeded
                    ? (session.UploadId, null)
                    : (null, current.Message ?? $"{file.Name} 上傳失敗，請重新選擇。");
            }
        }
    }

    /// <summary>從「待上傳」移除時呼叫：還在傳就取消；已經傳完的暫存檔由伺服器過期清掉。</summary>
    public Task CancelAsync(Guid uploadId) => api.CancelAsync(uploadId);
}

/// <summary><c>chunk-upload.js</c> 送不下去時呼叫（方法名稱 <c>OnFailed</c> 要與 JS 端的 <c>invokeMethodAsync</c> 一致）。</summary>
public sealed class AttachmentChunkCallback
{
    private readonly UploadApi api;
    private readonly Guid uploadId;
    private readonly ILogger logger;

    internal AttachmentChunkCallback(UploadApi api, Guid uploadId, ILogger logger)
    {
        this.api = api;
        this.uploadId = uploadId;
        this.logger = logger;
    }

    [JSInvokable]
    public async Task OnFailed(string message)
    {
        logger.LogWarning("Project attachment chunk upload failed. UploadId={UploadId}, Message={Message}", uploadId, message);
        await api.FailAsync(uploadId, string.IsNullOrWhiteSpace(message) ? "上傳失敗，請重新上傳。" : message);
    }
}
