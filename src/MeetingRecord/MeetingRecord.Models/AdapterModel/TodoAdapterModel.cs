using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class TodoAdapterModel : ICloneable
{
    /// <summary>狀態選項。順序即畫面下拉的順序，第一項為新增時的預設值。</summary>
    public static readonly IReadOnlyList<string> StatusOptions = ["待辦", "進行中", "已完成"];

    /// <summary>優先度選項。索引 1（中）為新增時的預設值。</summary>
    public static readonly IReadOnlyList<string> PriorityOptions = ["低", "中", "高"];

    /// <summary>代表「已完成」的狀態值。勾選完成與各處判斷都以此為準。</summary>
    public const string CompletedStatus = "已完成";

    public int Id { get; set; }

    [Required(ErrorMessage = "待辦標題 不可為空白")]
    [StringLength(200, ErrorMessage = "標題長度不可超過 200 字元")]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "所屬專案 不可為空白")]
    public int ProjectId { get; set; }

    /// <summary>所屬專案名稱。跨物件欄位，由服務層另外填入。</summary>
    public string? ProjectTitle { get; set; }

    public int? MeetingId { get; set; }

    /// <summary>來源會議紀錄標題。跨物件欄位，由服務層另外填入。</summary>
    public string? MeetingTitle { get; set; }

    [StringLength(50, ErrorMessage = "負責人長度不可超過 50 字元")]
    public string? Owner { get; set; }

    public DateTime? DueDate { get; set; }

    [Required(ErrorMessage = "優先度 不可為空白")]
    public string Priority { get; set; } = PriorityOptions[1];

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = StatusOptions[0];

    public List<string> Categories { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public string CategoriesText => string.Join("、", Categories);

    public string TeamsText => string.Join("、", Teams);

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    #region 顯示用計算屬性

    /// <summary>是否已完成</summary>
    public bool IsCompleted => string.Equals(Status, CompletedStatus, StringComparison.Ordinal);

    /// <summary>已逾期（有截止日、尚未完成、且截止日早於今天）</summary>
    public bool IsOverdue =>
        DueDate.HasValue
        && !IsCompleted
        && DueDate.Value.Date < DateTime.Today;

    /// <summary>逾期天數（未逾期時為 0）</summary>
    public int OverdueDays => IsOverdue ? (DateTime.Today - DueDate!.Value.Date).Days : 0;

    /// <summary>截止日顯示文字，逾期時附上天數</summary>
    public string DueDateText
    {
        get
        {
            if (!DueDate.HasValue)
            {
                return "未指定";
            }

            var text = DueDate.Value.ToString("yyyy/MM/dd");
            return IsOverdue ? $"{text}（逾期 {OverdueDays} 天）" : text;
        }
    }

    /// <summary>來源顯示文字。手動新增的待辦沒有來源會議紀錄。</summary>
    public string SourceText => string.IsNullOrWhiteSpace(MeetingTitle) ? "— 手動新增" : MeetingTitle;

    #endregion

    public TodoAdapterModel Clone()
    {
        var cloned = (TodoAdapterModel)((ICloneable)this).Clone();
        // MemberwiseClone 為淺複製，標籤清單需另建新實例，避免編輯中的修改回寫到清單資料列。
        cloned.Categories = [.. Categories];
        cloned.Teams = [.. Teams];
        return cloned;
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
