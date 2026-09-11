using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 待辦事項資料傳輸物件
/// </summary>
public class TodoCreateUpdateDto
{
    public TodoCreateUpdateDto()
    {
    }

    /// <summary>
    /// 待辦唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "待辦唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 待辦標題
    /// </summary>
    [Required(ErrorMessage = "待辦標題 不可為空白")]
    [StringLength(200, ErrorMessage = "標題長度不可超過 200 字元")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 待辦描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 所屬專案項目代碼（必填）
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "所屬專案 不可為空白")]
    [JsonPropertyName("projectId")]
    public int ProjectId { get; set; }

    /// <summary>
    /// 來源會議紀錄代碼（由 AI 抽出時填入，手動新增為 null）
    /// </summary>
    [JsonPropertyName("meetingId")]
    public int? MeetingId { get; set; }

    /// <summary>
    /// 負責人
    /// </summary>
    [StringLength(50, ErrorMessage = "負責人長度不可超過 50 字元")]
    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    /// <summary>
    /// 截止日
    /// </summary>
    [JsonPropertyName("dueDate")]
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// 優先度（低／中／高）
    /// </summary>
    [Required(ErrorMessage = "優先度 不可為空白")]
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    /// <summary>
    /// 狀態（待辦／進行中／已完成）
    /// </summary>
    [Required(ErrorMessage = "狀態 不可為空白")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

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
