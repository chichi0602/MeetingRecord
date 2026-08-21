namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 會議紀錄提示詞搜尋請求參數
/// </summary>
public class PromptTemplateSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 是否啟用篩選 (null 表示不篩選)
    /// </summary>
    public bool? IsEnabled { get; set; }
}
