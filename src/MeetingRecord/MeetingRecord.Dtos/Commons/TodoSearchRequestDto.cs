namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 待辦事項搜尋請求參數
/// </summary>
public class TodoSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 所屬專案篩選 (null 表示不篩選)
    /// </summary>
    public int? ProjectId { get; set; }

    /// <summary>
    /// 狀態篩選 (null 表示不篩選)
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// 優先度篩選 (null 表示不篩選)
    /// </summary>
    public string? Priority { get; set; }
}
