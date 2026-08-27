namespace MeetingRecord.Dtos.Commons;

/// <summary>
/// 會議紀錄搜尋請求參數
/// </summary>
public class MeetingSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 轉錄狀態篩選（0 未上傳、1 待處理、2 處理中、3 已完成、4 失敗；null 表示不篩選）
    /// </summary>
    public int? TranscriptionStatus { get; set; }
}
