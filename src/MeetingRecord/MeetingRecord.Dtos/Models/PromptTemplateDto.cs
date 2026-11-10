using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 會議紀錄提示詞資料傳輸物件
/// </summary>
public class PromptTemplateDto : PromptTemplateCreateUpdateDto
{
    /// <summary>清單「內容預覽」欄的長度上限（與 PromptTemplateAdapterModel 相同）。</summary>
    public const int ContentPreviewLength = 60;

    public PromptTemplateDto()
    {
    }

    /// <summary>提示詞內容的單行預覽（清單用，過長時截斷；不序列化），1.1.9。</summary>
    [JsonIgnore]
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

    /// <summary>編輯前複製一份（分類清單也複製），取消時不影響清單上的那筆。</summary>
    public PromptTemplateDto Clone()
    {
        var clone = (PromptTemplateDto)MemberwiseClone();
        clone.CategoryNames = CategoryNames is null ? null : [.. CategoryNames];
        return clone;
    }
}
