namespace MeetingRecord.Dtos.Uploads;

/// <summary>上傳的用途（1.1.16）。</summary>
public static class UploadPurposes
{
    public const string MeetingMedia = "MeetingMedia";

    /// <summary>專案附件（1.1.24，<c>POST api/v1/uploads/project-attachments</c>）：先傳到暫存檔，存專案時再帶編號。</summary>
    public const string ProjectAttachment = "ProjectAttachment";
}

/// <summary>建立上傳工作階段（<c>POST api/v1/uploads</c>，1.1.16）。</summary>
public class UploadSessionRequestDto
{
    public string Purpose { get; set; } = UploadPurposes.MeetingMedia;

    public int MeetingId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long Size { get; set; }

    /// <summary>這筆會議是這次存檔才新增的：取消上傳時連會議一起刪（同 Server 頁，1.0.37）。</summary>
    public bool DeleteMeetingOnCancel { get; set; }
}

/// <summary>建立好的上傳工作階段：瀏覽器照 <see cref="ChunkSize"/> 切塊，依序 <c>PUT api/v1/uploads/{uploadId}/chunks/{index}</c>。</summary>
public class UploadSessionDto
{
    public Guid UploadId { get; set; }

    public int ChunkSize { get; set; }

    public int ChunkCount { get; set; }
}

/// <summary>上傳工作階段的狀態值。</summary>
public static class UploadStates
{
    /// <summary>還在收檔或寫檔。</summary>
    public const string Uploading = "Uploading";

    /// <summary>存好了、已排入轉錄。</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>使用者取消（本頁、進度面板或 <c>DELETE</c>）。</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>失敗（例如瀏覽器放棄、逾時、檢查沒過）。</summary>
    public const string Failed = "Failed";
}

/// <summary>上傳工作階段目前的狀態（<c>GET api/v1/uploads/{uploadId}</c>）。</summary>
public class UploadStatusDto
{
    public Guid UploadId { get; set; }

    public int MeetingId { get; set; }

    public string State { get; set; } = UploadStates.Uploading;

    /// <summary>結束時服務層的訊息（例如「已取消上傳，這筆會議紀錄已一併移除。」）；成功時是空的。</summary>
    public string? Message { get; set; }

    /// <summary>取消時連會議一起刪了。</summary>
    public bool MeetingDeleted { get; set; }

    /// <summary>按了取消但檔案已經寫完（來不及取消），照樣排入轉錄。</summary>
    public bool CancelTooLate { get; set; }
}

/// <summary>
/// 瀏覽器送不下去（重試用完、登入失效）時回報原因（<c>POST api/v1/uploads/{uploadId}/fail</c>，1.1.23）：
/// 伺服器立刻把這趟上傳標成失敗、會議保留，比照 Server 頁；不回報的話要等 2 分鐘沒收到分塊才逾時。
/// </summary>
public class UploadFailRequestDto
{
    public string? Message { get; set; }
}
