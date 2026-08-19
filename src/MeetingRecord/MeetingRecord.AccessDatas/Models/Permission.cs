namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 權限鍵的目錄（資料驅動 RBAC 的權限主檔）。
/// 目前 Key 對應既有頁面權限名稱；階段四導入 resource:action 動作粒度。
/// </summary>
public class Permission
{
    public int Id { get; set; }
    /// <summary>唯一權限鍵。</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>顯示名稱。</summary>
    public string? DisplayName { get; set; }
    /// <summary>群組名稱（對應選單母項）。</summary>
    public string? GroupName { get; set; }
    /// <summary>排序。</summary>
    public int SortOrder { get; set; }
}
