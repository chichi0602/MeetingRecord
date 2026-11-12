using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 會議紀錄資料傳輸物件（讀取用，額外帶出影音檔與轉錄狀態）
/// </summary>
public class MeetingDto : MeetingCreateUpdateDto
{
    public MeetingDto()
    {
    }

    /// <summary>
    /// 影音檔原始檔名（尚未上傳時為 null）
    /// </summary>
    [JsonPropertyName("mediaOriginalFileName")]
    public string? MediaOriginalFileName { get; set; }

    /// <summary>
    /// 影音檔大小（位元組）
    /// </summary>
    [JsonPropertyName("mediaFileSize")]
    public long? MediaFileSize { get; set; }

    /// <summary>
    /// 轉錄狀態：0 未上傳、1 待處理、2 處理中、3 已完成、4 失敗
    /// </summary>
    [JsonPropertyName("transcriptionStatus")]
    public int TranscriptionStatus { get; set; }

    /// <summary>
    /// 轉錄失敗原因（成功時為 null）
    /// </summary>
    [JsonPropertyName("transcriptionError")]
    public string? TranscriptionError { get; set; }

    /// <summary>
    /// 轉錄完成時間
    /// </summary>
    [JsonPropertyName("transcriptionCompletedAt")]
    public DateTime? TranscriptionCompletedAt { get; set; }

    // ---- 1.1.20 起多帶的唯讀欄位：只加不改，舊的呼叫端照用 ----

    /// <summary>所屬專案；null＝未歸屬（只有上傳者與管理者看得到）。歸屬改用 <c>PUT {id}/project</c>。</summary>
    [JsonPropertyName("projectId")]
    public int? ProjectId { get; set; }

    [JsonPropertyName("projectTitle")]
    public string? ProjectTitle { get; set; }

    /// <summary>上傳者。</summary>
    [JsonPropertyName("createdByUserId")]
    public int? CreatedByUserId { get; set; }

    [JsonPropertyName("hasMedia")]
    public bool HasMedia { get; set; }

    [JsonPropertyName("hasTranscript")]
    public bool HasTranscript { get; set; }

    /// <summary>可以重新轉錄（有影音檔、沒有在轉錄或排隊）。</summary>
    [JsonPropertyName("canRetryTranscription")]
    public bool CanRetryTranscription { get; set; }

    /// <summary>會議紀錄（草稿）狀態，值同 Share 的 <c>DraftStatus</c> 列舉（0 尚未產生、1 排隊中、2 產生中…）。</summary>
    [JsonPropertyName("draftStatus")]
    public int DraftStatus { get; set; }

    [JsonPropertyName("draftError")]
    public string? DraftError { get; set; }

    [JsonPropertyName("draftPromptTemplateName")]
    public string? DraftPromptTemplateName { get; set; }

    [JsonPropertyName("draftCompletedAt")]
    public DateTime? DraftCompletedAt { get; set; }

    [JsonPropertyName("hasDraft")]
    public bool HasDraft { get; set; }

    /// <summary>可以產生（或重新產生）會議紀錄：有逐字稿、沒有在產生。</summary>
    [JsonPropertyName("canGenerateDraft")]
    public bool CanGenerateDraft { get; set; }

    /// <summary>會議紀錄全文。只有 <c>GET {id}</c> 會帶，清單不帶。</summary>
    [JsonPropertyName("draftContent")]
    public string? DraftContent { get; set; }

    /// <summary>呼叫者是上傳者（或管理者）：換影音檔、重新轉錄只限這些人。</summary>
    [JsonPropertyName("isUploader")]
    public bool IsUploader { get; set; }

    /// <summary>呼叫者能不能刪：上傳者、專案負責人或管理者，而且有會議紀錄的修改權限。只給畫面決定按鈕，真正的檢查在刪除 API。</summary>
    [JsonPropertyName("canDelete")]
    public bool CanDelete { get; set; }
}
