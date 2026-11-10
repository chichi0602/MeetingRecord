using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Web.Client.Uploads;

/// <summary>
/// WASM 端的會議影音檔上傳（1.1.23）。整個前端一份（WASM 的 Scoped 就是整個分頁），所以換到其他 WASM 頁上傳照樣繼續；
/// 關分頁、重新整理、換到還是 Blazor Server 的頁面才會中斷，前兩者靠 <c>beforeunload</c> 提醒、後者由 <c>ClientRoutes</c> 先確認。
///
/// <para>
/// 流程：<c>POST api/v1/uploads</c> 建工作階段（伺服器先跑上傳檢查）→ 既有的 <c>chunk-upload.js</c> 一塊一塊送 →
/// 每秒查一次 <c>GET api/v1/uploads/{id}</c> 直到結束 → 依結果跳通知（文字同 Server 頁）、觸發 <see cref="Changed"/> 讓清單重載。
/// 瀏覽器送不下去（重試用完、登入失效）就回報 <c>fail</c>，伺服器立刻結束這趟上傳、會議保留。
/// </para>
/// </summary>
public sealed class MeetingUploadManager
{
    /// <summary>按了取消，但檔案已經寫完。</summary>
    public const string UploadTooLateToCancelMessage =
        "影音檔已經上傳完成，來不及取消；如果不需要轉錄，可以在右下角的進度面板取消轉錄。";

    private const string KeepMeetingHint = "會議資料已保留，要重新上傳請按這一列的「修改」再選一次檔案。";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly UploadApi api;
    private readonly IJSRuntime js;
    private readonly NotificationService notifications;
    private readonly ILogger<MeetingUploadManager> logger;

    /// <summary>進行中的上傳，依會議編號。服務層同一筆會議一次只收一個檔（建立工作階段時就會擋下第二個）。</summary>
    private readonly Dictionary<int, ActiveUpload> uploads = [];

    public MeetingUploadManager(UploadApi api, ApiClient apiClient, IJSRuntime js, NotificationService notifications, ILogger<MeetingUploadManager> logger)
    {
        this.api = api;
        this.js = js;
        this.notifications = notifications;
        this.logger = logger;

        // 登入失效時 ApiClient 會整頁導到登入頁；上傳反正已經送不出去了，先拿掉離開提醒，否則瀏覽器會擋下這次導向。
        apiClient.BeforeLoginRedirect += () => SetLeaveGuardAsync(false);
    }

    /// <summary>有上傳開始或結束。結束時通知已經跳過了，訂閱者只要重新載入畫面。</summary>
    public event Action? Changed;

    public int ActiveCount => uploads.Count;

    /// <summary>這筆會議正在由這個分頁上傳（清單上的「取消上傳」只對這些出現，同 Server 頁「只在發起上傳的頁面」）。</summary>
    public bool IsUploading(int meetingId) => uploads.ContainsKey(meetingId);

    /// <summary>取消時會不會連會議一起刪（這筆是存檔時才新增的），確認框要照實講。</summary>
    public bool DeletesMeetingOnCancel(int meetingId) => uploads.TryGetValue(meetingId, out var upload) && upload.DeleteMeetingOnCancel;

    /// <summary>
    /// 開始上傳。<paramref name="input"/> 是選檔的 input：JS 在呼叫當下就拿到 File 物件，之後表單重設、元件釋放都不影響這趟上傳。
    /// 建立工作階段被擋下時回原因（呼叫端照 Server 頁補上「會議資料已保留」）；開始了回 null。
    /// </summary>
    public async Task<string?> StartAsync(ElementReference input, IBrowserFile file, int meetingId, bool deleteMeetingOnCancel)
    {
        var created = await api.CreateAsync(new UploadSessionRequestDto
        {
            Purpose = UploadPurposes.MeetingMedia,
            MeetingId = meetingId,
            FileName = file.Name,
            ContentType = file.ContentType,
            Size = file.Size,
            DeleteMeetingOnCancel = deleteMeetingOnCancel,
        });
        if (!created.Success || created.Data is not { } session)
        {
            return created.ErrorMessage ?? created.Message;
        }

        var upload = new ActiveUpload(session.UploadId, meetingId, deleteMeetingOnCancel);
        upload.Callback = DotNetObjectReference.Create(new ChunkUploadCallback(this, session.UploadId));
        uploads[meetingId] = upload;
        if (uploads.Count == 1)
        {
            await SetLeaveGuardAsync(true);
        }

        try
        {
            await js.InvokeVoidAsync("meetingRecordChunkUpload.start", input, 0, file.Name, file.Size, session.UploadId.ToString(), session.ChunkSize, upload.Callback);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Failed to start browser chunk upload. UploadId={UploadId}", session.UploadId);
            await ReportFailureAsync(session.UploadId, "瀏覽器無法開始上傳，請重新整理頁面後再試一次。");
        }

        Changed?.Invoke();
        _ = WatchAsync(upload);
        return null;
    }

    /// <summary>取消（等同 Server 頁的「取消上傳」）。已經結束的回 false，呼叫端照 Server 頁說「來不及取消」。</summary>
    public async Task<bool> CancelAsync(int meetingId)
    {
        if (!uploads.TryGetValue(meetingId, out var upload))
        {
            return false;
        }

        // 伺服器那一頭結束後，之後的分塊會收到 404，chunk-upload.js 自己收手；結果由 WatchAsync 照常通知。
        await api.CancelAsync(upload.UploadId);
        return true;
    }

    /// <summary>已確認要離開到 Blazor Server 頁（或登入失效）：拿掉關分頁提醒，免得整頁載入時瀏覽器再問一次。</summary>
    public Task ReleaseLeaveGuardAsync() => SetLeaveGuardAsync(false);

    internal async Task ReportFailureAsync(Guid uploadId, string message)
    {
        logger.LogWarning("Browser chunk upload failed. UploadId={UploadId}, Message={Message}", uploadId, message);

        // 連回報都送不出去（斷網）也沒關係：伺服器 2 分鐘沒收到分塊會自己判定中斷，WatchAsync 照樣會拿到結果。
        await api.FailAsync(uploadId, string.IsNullOrWhiteSpace(message) ? "上傳失敗，請重新上傳。" : message);
    }

    private async Task WatchAsync(ActiveUpload upload)
    {
        UploadStatusDto? status = null;
        while (status is null)
        {
            await Task.Delay(PollInterval);

            // 網路暫時中斷時拿到的是失敗結果（chunk-upload.js 也在重試）：下一輪再查。
            var result = await api.GetStatusAsync(upload.UploadId);
            if (result.StatusCode == 401)
            {
                return; // ApiClient 已導到登入頁。
            }

            if (result.StatusCode == 404)
            {
                status = new UploadStatusDto { State = UploadStates.Failed, Message = "找不到這趟上傳。" };
            }
            else if (result.Data is { } current && current.State != UploadStates.Uploading)
            {
                status = current;
            }
        }

        uploads.Remove(upload.MeetingId);
        upload.Callback?.Dispose();
        if (uploads.Count == 0)
        {
            await SetLeaveGuardAsync(false);
        }

        Notify(status);
        Changed?.Invoke();
    }

    /// <summary>結果通知，文字與種類同 Server 頁（成功、取消用「系統訊息」一般通知，失敗用錯誤通知）。</summary>
    private void Notify(UploadStatusDto status)
    {
        switch (status.State)
        {
            case UploadStates.Succeeded:
                NotifySuccess(status.CancelTooLate
                    ? UploadTooLateToCancelMessage
                    : "影音檔已上傳，系統已排入背景轉錄，進度會顯示在「轉錄狀態」。");
                break;
            case UploadStates.Cancelled:
                NotifySuccess(status.MeetingDeleted && !string.IsNullOrEmpty(status.Message)
                    ? status.Message
                    : "已取消上傳。" + KeepMeetingHint);
                break;
            default:
                NotifyError($"{status.Message}{KeepMeetingHint}");
                break;
        }
    }

    private void NotifySuccess(string description)
        => _ = notifications.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });

    private void NotifyError(string description)
        => _ = notifications.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight,
            Duration = 8,
        });

    private async Task SetLeaveGuardAsync(bool active)
    {
        try
        {
            await js.InvokeVoidAsync("meetingRecordChunkUpload.setLeaveGuard", active);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Failed to toggle the leave guard.");
        }
    }

    private sealed class ActiveUpload(Guid uploadId, int meetingId, bool deleteMeetingOnCancel)
    {
        public Guid UploadId { get; } = uploadId;

        public int MeetingId { get; } = meetingId;

        public bool DeleteMeetingOnCancel { get; } = deleteMeetingOnCancel;

        public DotNetObjectReference<ChunkUploadCallback>? Callback { get; set; }
    }
}

/// <summary><c>chunk-upload.js</c> 送不下去時呼叫（方法名稱與 Server 頁的 <c>BrowserUploadCallback</c> 相同）。</summary>
public sealed class ChunkUploadCallback
{
    private readonly MeetingUploadManager manager;
    private readonly Guid uploadId;

    internal ChunkUploadCallback(MeetingUploadManager manager, Guid uploadId)
    {
        this.manager = manager;
        this.uploadId = uploadId;
    }

    [JSInvokable]
    public Task OnFailed(string message) => manager.ReportFailureAsync(uploadId, message);
}
