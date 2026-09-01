namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 待辦事項資料傳輸物件（含唯讀的關聯名稱）
/// </summary>
public class TodoDto : TodoCreateUpdateDto
{
    public TodoDto()
    {
    }

    /// <summary>所屬專案名稱（唯讀）</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>來源會議紀錄標題（唯讀）</summary>
    public string? MeetingTitle { get; set; }
}
