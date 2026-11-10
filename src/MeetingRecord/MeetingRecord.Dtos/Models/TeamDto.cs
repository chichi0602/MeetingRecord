using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 團隊資料傳輸物件
/// </summary>
public class TeamDto : TeamCreateUpdateDto
{
    public TeamDto()
    {
    }

    /// <summary>成員數（清單的「成員數」欄，1.1.8）。</summary>
    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }

    /// <summary>編輯前複製一份（成員清單也複製），取消時不影響清單上的那筆。</summary>
    public TeamDto Clone()
    {
        var clone = (TeamDto)MemberwiseClone();
        clone.MemberIds = MemberIds is null ? null : [.. MemberIds];
        return clone;
    }
}
