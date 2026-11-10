using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Dtos.Users;

/// <summary>使用者管理搜尋（1.1.10）：關鍵字比對帳號、名稱、Email、主要角色名稱。</summary>
public sealed class UserSearchRequestDto : SearchRequestBaseDto
{
}

/// <summary>角色下拉的選項（1.1.10）。<see cref="IsDefault"/>＝新建帳號預設的角色。</summary>
public sealed class RoleOptionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}

/// <summary>批次把勾選的人加入（<see cref="Add"/>＝true）或移出一個團隊（1.1.10）。</summary>
public sealed class UserTeamBatchRequestDto
{
    public List<int> UserIds { get; set; } = [];

    public int TeamId { get; set; }

    public bool Add { get; set; }
}
