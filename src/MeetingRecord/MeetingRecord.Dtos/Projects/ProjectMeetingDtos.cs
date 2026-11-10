namespace MeetingRecord.Dtos.Projects;

/// <summary>專案頁「AI 轉會議紀錄」可選的逐字稿（轉錄完成、看得到的會議，1.1.25）。</summary>
public class ProjectTranscriptOptionDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime? MeetingDate { get; set; }

    /// <summary>已歸屬的專案（null＝未歸屬）。</summary>
    public int? ProjectId { get; set; }

    public string? ProjectTitle { get; set; }

    /// <summary>已經有會議紀錄（1.1.26）：選它來產生會覆蓋既有內容，畫面要照實提醒。</summary>
    public bool HasDraft { get; set; }

    /// <summary>會議紀錄狀態，值同 Share 的 <c>DraftStatus</c>。</summary>
    public int DraftStatus { get; set; }

    /// <summary>可以產生（或重新產生）：有逐字稿、沒有在產生。</summary>
    public bool CanGenerateDraft { get; set; }
}
