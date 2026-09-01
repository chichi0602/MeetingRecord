using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

public class Project
{
    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = string.Empty;

    [Required(ErrorMessage = "優先級 不可為空白")]
    public string Priority { get; set; } = string.Empty;

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>分類標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Categories { get; set; }

    /// <summary>團隊標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Teams { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ICollection<ProjectFile> Files { get; set; } = [];

    /// <summary>
    /// 歸屬於本專案的會議紀錄（逐字稿與 AI 草稿）。
    /// 與 Files 不同，刪除專案時不串連刪除，只把 Meeting.ProjectId 設為 null。
    /// </summary>
    public ICollection<Meeting> Meetings { get; set; } = [];

    /// <summary>
    /// 本專案的待辦事項。與 Meetings 不同，刪除專案時一併刪除——
    /// 待辦沒有專案就沒有意義。
    /// </summary>
    public ICollection<Todo> Todos { get; set; } = [];
}
