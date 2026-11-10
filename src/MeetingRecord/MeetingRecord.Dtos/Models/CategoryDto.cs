using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 分類資料傳輸物件
/// </summary>
public class CategoryDto : CategoryCreateUpdateDto
{
    public CategoryDto()
    {
    }

    /// <summary>負責團隊名稱（依名稱排序，與 <see cref="CategoryCreateUpdateDto.TeamIds"/> 同順序），1.1.7。</summary>
    [JsonPropertyName("teamNames")]
    public List<string> TeamNames { get; set; } = [];

    /// <summary>清單顯示用（不序列化）。</summary>
    [JsonIgnore]
    public string TeamNamesText => TeamNames.Count > 0 ? string.Join("、", TeamNames) : "—";

    /// <summary>編輯前複製一份（團隊清單也複製），取消時不影響清單上的那筆。</summary>
    public CategoryDto Clone()
    {
        var clone = (CategoryDto)MemberwiseClone();
        clone.TeamIds = TeamIds is null ? null : [.. TeamIds];
        clone.TeamNames = [.. TeamNames];
        return clone;
    }
}
