using System.ComponentModel.DataAnnotations;
using MeetingRecord.Dtos.Commons;

namespace MeetingRecord.Dtos.Roles;

/// <summary>
/// 角色管理（1.1.11，<c>api/v1/Role</c>，只限管理者）。清單不帶權限樹（<see cref="RolePermission"/> 為 null），單筆與新增、修改才帶。
/// </summary>
public class RoleDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    public DateTime CreateAt { get; set; }

    public DateTime UpdateAt { get; set; }

    /// <summary>權限矩陣（群組 → 頁面 → 動作）。伺服器存檔時轉成權限鍵，並檢查每個名稱都在權限目錄裡。</summary>
    public RolePermissionDto? RolePermission { get; set; }

    /// <summary>編輯前複製一份（權限樹也複製），取消時不影響清單上的那筆。</summary>
    public RoleDto Clone()
    {
        var clone = (RoleDto)MemberwiseClone();
        clone.RolePermission = RolePermission?.Clone();
        return clone;
    }
}

/// <summary>權限矩陣（欄位與伺服器的 <c>Models.Admins.RolePermission</c> 相同）。</summary>
public class RolePermissionDto
{
    public List<RolePermissionGroupDto> Groups { get; set; } = [];

    public RolePermissionDto Clone() => new() { Groups = Groups.Select(x => x.Clone()).ToList() };
}

public class RolePermissionGroupDto
{
    public string Name { get; set; } = string.Empty;

    public bool Enable { get; set; }

    public List<RolePermissionNodeDto> Permissions { get; set; } = [];

    public RolePermissionGroupDto Clone() => new() { Name = Name, Enable = Enable, Permissions = Permissions.Select(x => x.Clone()).ToList() };
}

public class RolePermissionNodeDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>整頁鍵＝該頁全部動作（勾了之後動作明細停用）。</summary>
    public bool Enable { get; set; }

    /// <summary>動作代碼（view/create/edit/delete/export）→ 是否勾選。</summary>
    public Dictionary<string, bool> Actions { get; set; } = [];

    public RolePermissionNodeDto Clone() => new() { Name = Name, Enable = Enable, Actions = new Dictionary<string, bool>(Actions) };
}

/// <summary>角色管理搜尋（1.1.11）：關鍵字比對名稱。</summary>
public sealed class RoleSearchRequestDto : SearchRequestBaseDto
{
}
