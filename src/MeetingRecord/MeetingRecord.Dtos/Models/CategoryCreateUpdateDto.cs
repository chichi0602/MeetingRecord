using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 分類資料傳輸物件
/// </summary>
public class CategoryCreateUpdateDto
{
    public CategoryCreateUpdateDto()
    {
    }

    /// <summary>
    /// 分類唯一代碼 (僅更新時使用)
    /// </summary>
    [Required(ErrorMessage = "分類唯一代碼 不可為空白")]
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 分類名稱
    /// </summary>
    [Required(ErrorMessage = "分類名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 分類描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 是否啟用
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

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
