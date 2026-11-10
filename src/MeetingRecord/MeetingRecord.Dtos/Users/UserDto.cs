using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Users;

/// <summary>使用者管理清單與單筆（1.1.10）。不含任何密碼相關欄位（<see cref="UserCreateUpdateDto.Password"/> 一律是 null，不會序列化）。</summary>
public class UserDto : UserCreateUpdateDto
{
    /// <summary>所有角色名稱（主要＋額外，依名稱排序），清單「角色」欄用。</summary>
    public List<string> RoleNames { get; set; } = [];

    public DateTime CreateAt { get; set; }

    public DateTime UpdateAt { get; set; }

    [JsonIgnore]
    public string StatusText => Status ? "啟用" : "停用";

    /// <summary>清單「角色」欄（同 MyUserAdapterModel.RoleText）。</summary>
    [JsonIgnore]
    public string RoleText
    {
        get
        {
            var roles = RoleNames.Count > 0 ? string.Join("、", RoleNames) : "—";
            return IsAdmin ? $"管理者（{roles}）" : roles;
        }
    }

    /// <summary>清單「團隊」欄。</summary>
    [JsonIgnore]
    public string TeamNamesText => TeamNames is { Count: > 0 } ? string.Join("、", TeamNames) : "—";

    /// <summary>編輯前複製一份（清單也複製），取消時不影響清單上的那筆。</summary>
    public UserDto Clone()
    {
        var clone = (UserDto)MemberwiseClone();
        clone.AdditionalRoleIds = AdditionalRoleIds is null ? null : [.. AdditionalRoleIds];
        clone.TeamNames = TeamNames is null ? null : [.. TeamNames];
        clone.RoleNames = [.. RoleNames];
        return clone;
    }
}
