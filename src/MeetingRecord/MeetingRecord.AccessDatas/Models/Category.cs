using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 分類（主資料，獨立無外鍵關聯）
/// </summary>
public class Category
{
    public int Id { get; set; }

    [Required(ErrorMessage = "分類名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>是否啟用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
