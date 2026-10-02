namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 分類「通常由哪些團隊負責」（0.4.102）。<b>只是參考資訊</b>：不影響專案的團隊，也不影響誰看得到。
/// </summary>
public class CategoryTeam
{
    public int Id { get; set; }

    public int CategoryId { get; set; }

    public Category? Category { get; set; }

    public int TeamId { get; set; }

    public Team? Team { get; set; }
}
