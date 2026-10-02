using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class CategoryAdapterModel : ICloneable
{
    public int Id { get; set; }

    [Required(ErrorMessage = "分類名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 通常負責這個分類的團隊（0.4.102，<c>CategoryTeam</c>）。<b>只是參考資訊</b>：
    /// 不會帶進專案，也不影響誰看得到。
    /// </summary>
    public List<int> TeamIds { get; set; } = new();

    /// <summary>負責團隊名稱，清單顯示用。</summary>
    public List<string> TeamNames { get; set; } = new();

    public string TeamNamesText => TeamNames.Count > 0 ? string.Join("、", TeamNames) : "—";

    public CategoryAdapterModel Clone()
    {
        return (CategoryAdapterModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        var clone = (CategoryAdapterModel)MemberwiseClone();
        clone.TeamIds = new List<int>(TeamIds);
        clone.TeamNames = new List<string>(TeamNames);
        return clone;
    }
}
