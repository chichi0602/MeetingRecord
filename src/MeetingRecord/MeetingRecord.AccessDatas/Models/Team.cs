using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 團隊（主資料，獨立無外鍵關聯）
/// </summary>
public class Team
{
    public int Id { get; set; }

    [Required(ErrorMessage = "團隊名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    /// <summary>團隊代號（選填，有填則需唯一）</summary>
    public string? Code { get; set; }

    public string? Description { get; set; }

    /// <summary>是否啟用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
