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
}
