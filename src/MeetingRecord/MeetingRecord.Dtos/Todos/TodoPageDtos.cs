namespace MeetingRecord.Dtos.Todos;

/// <summary>
/// 待辦清單頁的查詢（<c>POST api/v1/Todo/tree</c>，1.1.18）：只分頁主層，篩選命中子項目時也列出它的主待辦，子項目跟著主待辦。
/// 與對外的平面搜尋（<c>POST api/Todo/search</c>）不同形狀，所以另開端點。
/// </summary>
public class TodoTreeRequestDto
{
    public string? Keyword { get; set; }

    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 10;

    /// <summary>排序欄位：Title、ProjectTitle、Owner、DueDate、Priority、Status、CreatedAt、UpdatedAt；空的＝有截止日的在前、越早到期越前面。</summary>
    public string? SortBy { get; set; }

    /// <summary>null＝不排序（用預設）。</summary>
    public bool? SortDescending { get; set; }

    public int? ProjectId { get; set; }

    public string? Status { get; set; }

    /// <summary>「使用中｜已完成｜全部」：Active、Archived、All（看待辦所屬專案是不是已完成）。</summary>
    public string ProjectScope { get; set; } = "All";
}

/// <summary>右側負責人面板的一列統計（<c>GET api/v1/Todo/owners</c>）。</summary>
public class TodoOwnerSummaryDto
{
    public string Owner { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Completed { get; set; }

    public int InProgress { get; set; }

    public int Pending { get; set; }

    public int Overdue { get; set; }

    /// <summary>完成度（0～100），整數截斷：199/200 不會顯示成 100%。</summary>
    public int CompletionPercent => Total == 0 ? 0 : Completed * 100 / Total;
}

/// <summary>待辦的一筆異動紀錄（<c>GET api/v1/Todo/{id}/changes</c>）。</summary>
public class TodoChangeDto
{
    public int Id { get; set; }

    public string Field { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? ChangedByName { get; set; }

    public DateTime ChangedAt { get; set; }

    public string Via { get; set; } = string.Empty;

    public int? SourceMeetingId { get; set; }

    public string? SourceMeetingTitle { get; set; }

    public string? Note { get; set; }
}

/// <summary>待辦頁可選的專案（篩選與表單用，<c>GET api/v1/Todo/projects/selectable</c>）。</summary>
public class TodoProjectOptionDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Status { get; set; }

    /// <summary>已完成＝封存：表單不能選（正在編輯的那筆原本就在封存專案時例外）。</summary>
    public bool IsArchived { get; set; }
}

/// <summary>可選的主待辦（<c>GET api/v1/Todo/parent-candidates</c>）：同專案、本身不是子項目、未完成、不是自己。</summary>
public class TodoParentOptionDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int ProjectId { get; set; }
}

/// <summary>勾選完成（<c>PUT api/v1/Todo/{id}/completed</c>）。</summary>
public class TodoCompletedRequestDto
{
    public bool IsCompleted { get; set; }
}
