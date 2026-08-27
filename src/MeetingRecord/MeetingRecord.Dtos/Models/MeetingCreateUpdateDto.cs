using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 會議紀錄資料傳輸物件（僅中繼資料；影音檔上傳與轉錄不經由 Web API）
/// </summary>
public class MeetingCreateUpdateDto
{
    public MeetingCreateUpdateDto()
    {
    }

    /// <summary>
    /// 會議紀錄唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "會議紀錄唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 會議標題
    /// </summary>
    [Required(ErrorMessage = "會議標題 不可為空白")]
    [StringLength(200, ErrorMessage = "會議標題長度不可超過 200 字元")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 會議日期
    /// </summary>
    [JsonPropertyName("meetingDate")]
    public DateTime? MeetingDate { get; set; }

    /// <summary>
    /// 會議描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 分類標籤（多值，以分隔字串儲存，可空）
    /// </summary>
    [JsonPropertyName("categories")]
    public string? Categories { get; set; }

    /// <summary>
    /// 團隊標籤（多值，以分隔字串儲存，可空；空值表示公開）
    /// </summary>
    [JsonPropertyName("teams")]
    public string? Teams { get; set; }

    /// <summary>
    /// 建立時間
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// 更新時間
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
