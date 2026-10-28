namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 站內通知（1.0.44），右上角鈴鐺。都是待辦的異動：期限／負責人／狀態被改了，收件人是待辦所屬專案的
/// 主責＋協作團隊成員；1.0.55 起甲方帳號的任何修改（含新增、刪除）另外通知專案負責人。都不含改的人自己。
/// 使用者刪除時一併刪除；過了保留期限（<c>LogRetentionSettings.NotificationDays</c>）自動清除。
/// </summary>
public class UserNotification
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public MyUser? User { get; set; }

    /// <summary>相關待辦（無 FK：待辦刪了通知照樣留著，點了會提示已刪除）。</summary>
    public int? TodoId { get; set; }

    public int? ProjectId { get; set; }

    /// <summary>通知內容，例：「王小明 把『完成報表』的期限改為 2026/10/31（來源：週會）」。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>建立時間（本地時間）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>已讀時間；null＝未讀。</summary>
    public DateTime? ReadAt { get; set; }
}
