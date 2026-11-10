using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.Users;

/// <summary>
/// 使用者管理的新增／修改內容（1.1.10，<c>api/v1/User</c>，只限管理者）。
/// ⚠️ 密碼只送進來、伺服器永遠不回（<see cref="Password"/> 為 null 時不序列化）；密碼雜湊、Salt、兩步驟驗證金鑰、Google 編號都不在這裡。
/// </summary>
public class UserCreateUpdateDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = string.Empty;

    /// <summary>新增時必填；修改時留白＝沿用既有密碼，有填＝重設（替別人重設時對方下次登入要改）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>啟用（可以登入）。</summary>
    public bool Status { get; set; } = true;

    /// <summary>管理者：不看角色與團隊，擁有全部功能、看得到所有資料。</summary>
    public bool IsAdmin { get; set; }

    /// <summary>甲方。</summary>
    public bool IsExternal { get; set; }

    /// <summary>主要角色。</summary>
    [Required(ErrorMessage = "角色 不可為空白")]
    public int? RoleViewId { get; set; }

    /// <summary>額外角色（權限取聯集）。修改時 null＝不動、空陣列＝清空。</summary>
    public List<int>? AdditionalRoleIds { get; set; }

    /// <summary>所屬團隊名稱（決定看得到哪些專案）。修改時 null＝不動、空陣列＝清空。</summary>
    public List<string>? TeamNames { get; set; }
}
