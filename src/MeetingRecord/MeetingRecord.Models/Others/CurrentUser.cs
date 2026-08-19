using System.Reflection;
using System.Text.Json;

namespace MeetingRecord.Models.Others;

public class CurrentUser
{
    public CurrentUser()
    {
    }
    public int Id { get; set; }
    public string Account { get; set; } = String.Empty;
    public string Name { get; set; } = String.Empty;
    public bool Status { get; set; } = true;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; } = false;
    public string RoleJson { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; } = false;
    public List<string> RoleList { get; set; } = new();
    /// <summary>角色的預設團隊清單（用於紀錄團隊權限過濾）</summary>
    public List<string> TeamList { get; set; } = new();

    public void CopyFrom(CurrentUser source)
    {
        foreach (PropertyInfo property in typeof(CurrentUser).GetProperties())
        {
            if (property.CanWrite)
            {
                property.SetValue(this, property.GetValue(source));
            }
        }

        RoleList = JsonSerializer.Deserialize<List<string>>(this.RoleJson) ?? new List<string>();
    }
}
