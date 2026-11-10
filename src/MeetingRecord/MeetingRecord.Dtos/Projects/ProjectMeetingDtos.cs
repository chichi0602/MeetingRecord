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
}
