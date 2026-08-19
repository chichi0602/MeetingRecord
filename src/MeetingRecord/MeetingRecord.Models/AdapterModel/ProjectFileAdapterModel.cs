namespace MeetingRecord.Models.AdapterModel;

public class ProjectFileAdapterModel
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; }
}
