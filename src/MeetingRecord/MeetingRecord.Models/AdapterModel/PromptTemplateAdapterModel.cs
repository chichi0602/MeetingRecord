using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class PromptTemplateAdapterModel : ICloneable
{
    /// <summary>清單「內容預覽」欄位的最大顯示字數</summary>
    public const int ContentPreviewLength = 60;

    public int Id { get; set; }

    [Required(ErrorMessage = "提示詞名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱長度不可超過 100 字元")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "提示詞內容 不可為空白")]
    [StringLength(20000, ErrorMessage = "提示詞內容長度不可超過 20000 字元")]
    public string Content { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public List<string> Categories { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public string CategoriesText => string.Join("、", Categories);

    public string TeamsText => string.Join("、", Teams);

    /// <summary>提示詞內容的單行預覽（供清單欄位顯示，過長時截斷）</summary>
    public string ContentPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content))
            {
                return string.Empty;
            }

            var singleLine = Content.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return singleLine.Length <= ContentPreviewLength
                ? singleLine
                : string.Concat(singleLine.AsSpan(0, ContentPreviewLength), "…");
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public PromptTemplateAdapterModel Clone()
    {
        var cloned = (PromptTemplateAdapterModel)((ICloneable)this).Clone();
        // MemberwiseClone 為淺複製，標籤清單需另建新實例，避免編輯中的修改回寫到清單資料列。
        cloned.Categories = [.. Categories];
        cloned.Teams = [.. Teams];
        return cloned;
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
