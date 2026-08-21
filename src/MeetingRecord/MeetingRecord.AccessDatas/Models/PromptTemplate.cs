using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 會議紀錄提示詞範本（主資料，獨立無外鍵關聯）
/// </summary>
public class PromptTemplate
{
    public int Id { get; set; }

    [Required(ErrorMessage = "提示詞名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    /// <summary>提示詞內容（長文字，可含 {{transcript}} 等變數佔位符）</summary>
    [Required(ErrorMessage = "提示詞內容 不可為空白")]
    public string Content { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>是否啟用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>分類標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Categories { get; set; }

    /// <summary>團隊標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Teams { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
