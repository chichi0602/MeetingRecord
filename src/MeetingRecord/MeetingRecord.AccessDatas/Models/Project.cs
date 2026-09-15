using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

public class Project
{
    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = string.Empty;

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// 常用名詞（術語、產品名）。AI 產生會議紀錄時會連同與會者名單一起餵給模型，
    /// 讓它把語音辨識聽錯的同音字改回正確寫法。
    ///
    /// ⚠️ 儲存格式是 <c>TagStringHelper</c> 的換行包夾字串（<c>"\nA\nB\n"</c>），
    /// 不是逗號分隔。讀寫一律經 <c>TagStringHelper.ToList</c> / <c>ToStored</c>。
    /// </summary>
    public string? GlossaryTerms { get; set; }

    /// <summary>
    /// 常用與會人員名冊。是「這個專案常出現的人」的長期清單，不是某一場會議的實到名單——
    /// 每次產生會議紀錄時從這裡勾選實際到場的人，勾選結果存在 <c>Meeting.DraftAttendees</c>。
    ///
    /// ⚠️ 儲存格式同 <see cref="GlossaryTerms"/>。
    /// </summary>
    public string? Participants { get; set; }

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
