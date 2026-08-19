namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 分類搜尋請求參數
/// </summary>
public class CategorySearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 是否啟用篩選 (null 表示不篩選)
    /// </summary>
    public bool? IsEnabled { get; set; }
}
