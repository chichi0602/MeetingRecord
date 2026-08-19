using MeetingRecord.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class RoleViewAdapterModel : ICloneable
{
    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string TabViewJson { get; set; } = string.Empty;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    public RolePermission RolePermission { get; set; } = new();
    /// <summary>角色預設團隊（UI 綁定用）</summary>
    public List<string> DefaultTeams { get; set; } = [];

    public RoleViewAdapterModel Clone()
    {
        return (RoleViewAdapterModel)((ICloneable)this).Clone();
    }
    object ICloneable.Clone()
    {
        return new RoleViewAdapterModel
        {
            Id = Id,
            Name = Name,
            TabViewJson = TabViewJson,
            CreateAt = CreateAt,
            UpdateAt = UpdateAt,
            RolePermission = RolePermission.Clone(),
            DefaultTeams = new List<string>(DefaultTeams)
        };
    }
}
