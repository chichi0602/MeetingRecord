using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 待辦事項。
///
/// 一定隸屬於一個專案項目（<see cref="ProjectId"/> 必填，刪除專案時一併刪除）；
/// 若是從會議紀錄抽出來的，<see cref="MeetingId"/> 會指向來源會議，
/// 手動新增則為 null。
///
/// 完成與否只看 <see cref="Status"/> 一個欄位——刻意不另存 IsCompleted 布林，
/// 兩個欄位並存必然會出現「勾了完成但狀態還是進行中」這種互相矛盾的資料。
/// </summary>
public class Todo
{
    public int Id { get; set; }

    [Required(ErrorMessage = "待辦標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    #region 關聯

    /// <summary>所屬專案項目。必填——沒有專案的待辦只會變成沒人管的垃圾資料。</summary>
    [Required(ErrorMessage = "所屬專案 不可為空白")]
    public int ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>來源會議紀錄。由 AI 從會議紀錄抽出時填入，手動新增為 null。</summary>
    public int? MeetingId { get; set; }

    public Meeting? Meeting { get; set; }

    #endregion

    /// <summary>負責人</summary>
    public string? Owner { get; set; }

    /// <summary>截止日（可空）</summary>
    public DateTime? DueDate { get; set; }

    [Required(ErrorMessage = "優先度 不可為空白")]
    public string Priority { get; set; } = string.Empty;

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
