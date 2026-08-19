using MeetingRecord.Models.Admins;
using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class MyUserAdapterModel : ICloneable
{
    public int Id { get; set; }
    [Required(ErrorMessage = "帳號 不可為空白")]
    public string Account { get; set; } = String.Empty;
    public string Password { get; set; } = String.Empty;
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = String.Empty;
    public string? Salt { get; set; }
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
    [Required(ErrorMessage = "角色 不可為空白")]
    public int? RoleViewId { get; set; }
    public string? OAuthProvider { get; set; }
    public string? GoogleId { get; set; }
    public RoleViewAdapterModel? RoleView { get; set; }
    /// <summary>額外角色（主要角色 RoleViewId 之外）；與主要角色一起寫入 UserRole（多角色）。</summary>
    public List<int> AdditionalRoleIds { get; set; } = new();
    /// <summary>直接綁在使用者的團隊名稱；寫入 UserTeam（團隊綁使用者）。</summary>
    public List<string> TeamNames { get; set; } = new();
    public string RoleViewName => RoleView?.Name ?? string.Empty;
    public string StatusText => Status ? "啟用" : "停用";
    public string IsAdminText => IsAdmin ? "是" : "否";
    public bool IsGoogleAccount => string.Equals(OAuthProvider, "Google", StringComparison.OrdinalIgnoreCase);

    public MyUserAdapterModel Clone()
    {
        return (MyUserAdapterModel)((ICloneable)this).Clone();
    }
    object ICloneable.Clone()
    {
        return this.MemberwiseClone();
    }
}
