namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 稽核軌跡：記錄「誰、何時、對什麼、做了什麼、結果」。
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    /// <summary>
    /// 事件發生時間（UTC）。
    /// </summary>
    public DateTime OccurredAt { get; set; }
    /// <summary>
    /// 操作者使用者 Id；系統或匿名事件可為 null。
    /// </summary>
    public int? ActorUserId { get; set; }
    /// <summary>
    /// 操作者帳號（快照，避免使用者刪除後無法追溯）。
    /// </summary>
    public string? ActorAccount { get; set; }
    /// <summary>
    /// 動作代碼，例如 Login.Success / Login.Failed / User.Create。
    /// </summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>
    /// 目標實體類型，例如 MyUser、RoleView。
    /// </summary>
    public string? TargetType { get; set; }
    /// <summary>
    /// 目標實體識別（通常為主鍵字串）。
    /// </summary>
    public string? TargetId { get; set; }
    /// <summary>
    /// 補充摘要（非敏感資訊）。
    /// </summary>
    public string? Detail { get; set; }
    /// <summary>
    /// 該操作是否成功。
    /// </summary>
    public bool Success { get; set; } = true;
}
