namespace MeetingRecord.Dtos.Notifications;

/// <summary>站內通知的一則（1.1.6，<c>GET /api/v1/notifications</c>）。</summary>
public class NotificationDto
{
    public int Id { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>已讀時間；null＝未讀。</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>相關的待辦；點通知會開到待辦頁的這一筆。</summary>
    public int? TodoId { get; set; }
}
