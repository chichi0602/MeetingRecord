using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

public class ProjectFile
{
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    [Required]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    public string StoredFileName { get; set; } = string.Empty;

    [Required]
    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Project? Project { get; set; }
}
