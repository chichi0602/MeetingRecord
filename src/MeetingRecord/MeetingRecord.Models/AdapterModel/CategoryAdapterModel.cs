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

    public CategoryAdapterModel Clone()
    {
        return (CategoryAdapterModel)((ICloneable)this).Clone();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
