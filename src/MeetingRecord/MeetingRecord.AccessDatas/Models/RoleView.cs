using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 使用者
/// </summary>
public class RoleView
{
    public RoleView()
    {
    }
    public int Id { get; set; }
    [Required(ErrorMessage = "名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;
    [Required(ErrorMessage = "頁面可視權限 Json 不可為空白")]
    public string TabViewJson { get; set; } = string.Empty;
    /// <summary>角色預設團隊（JSON 字串陣列，例如 ["團隊A","團隊B"]）</summary>
    public string DefaultTeamsJson { get; set; } = "[]";
    public DateTime CreateAt { get; set; } = DateTime.Now;
    public DateTime UpdateAt { get; set; } = DateTime.Now;
}
