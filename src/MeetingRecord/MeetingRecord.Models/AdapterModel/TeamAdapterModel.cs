using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class TeamAdapterModel : ICloneable
{
    public int Id { get; set; }

    [Required(ErrorMessage = "團隊名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "代號長度不可超過 50 字元")]
    public string? Code { get; set; }

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>成員（UserTeam）的使用者 Id；團隊頁編輯時載入、存檔時同步（0.4.101）。</summary>
    public List<int> MemberIds { get; set; } = new();

    /// <summary>清單的「成員數」欄。</summary>
    public int MemberCount { get; set; }

    public TeamAdapterModel Clone()
    {
        return (TeamAdapterModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        var clone = (TeamAdapterModel)MemberwiseClone();
        clone.MemberIds = new List<int>(MemberIds);
        return clone;
    }
}
