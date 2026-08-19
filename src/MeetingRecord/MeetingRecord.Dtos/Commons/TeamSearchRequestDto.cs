namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 團隊搜尋請求參數
/// </summary>
public class TeamSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 是否啟用篩選 (null 表示不篩選)
    /// </summary>
    public bool? IsEnabled { get; set; }
}
