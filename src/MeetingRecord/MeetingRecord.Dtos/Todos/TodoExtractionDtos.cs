namespace MeetingRecord.Dtos.Todos;

/// <summary>
/// 從會議紀錄抽出待辦的結果（<c>POST api/v1/todo-extraction/meetings/{meetingId}</c>，1.1.25）。會花 AI 費用。
/// 先前已加入過的標題與本專案未完成的待辦一起回傳，畫面用來標示「已加入過」「對應既有」（同 Server 頁的確認視窗）。
/// </summary>
public class TodoExtractionResultDto
{
    public List<ExtractedTodoDto> Items { get; set; } = [];

    /// <summary>這場會議先前已加入過的待辦標題（比對忽略大小寫）。</summary>
    public List<string> ExistingTitles { get; set; } = [];

    /// <summary>本專案未完成的待辦（送給 AI 比對的同一份）。</summary>
    public List<OpenTodoSummaryDto> OpenTodos { get; set; } = [];
}

/// <summary>AI 抽出的一條待辦候選。<see cref="MatchedTodoId"/>＝同一件事的既有待辦；<see cref="ParentTodoId"/>＝建議掛在哪筆主待辦底下。</summary>
public class ExtractedTodoDto
{
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Owner { get; set; }

    public DateTime? DueDate { get; set; }

    public string Priority { get; set; } = string.Empty;

    public int? MatchedTodoId { get; set; }

    public int? ParentTodoId { get; set; }
}

/// <summary>本專案未完成的待辦摘要。</summary>
public class OpenTodoSummaryDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Owner { get; set; }

    public DateTime? DueDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? MeetingTitle { get; set; }

    public bool IsSubtask { get; set; }
}

/// <summary>把一條候選加為新待辦（<c>POST api/v1/todo-extraction/todos</c>）；稽核來源記 <c>via=AiExtract</c>。</summary>
public class ExtractedTodoAddRequestDto
{
    public int ProjectId { get; set; }

    public int MeetingId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Owner { get; set; }

    public DateTime? DueDate { get; set; }

    public string Priority { get; set; } = string.Empty;

    /// <summary>加為子項目：掛到這筆主待辦底下（服務層會再檢查只分一層、同專案）。</summary>
    public int? ParentTodoId { get; set; }
}

/// <summary>用候選更新既有待辦（<c>PUT api/v1/todo-extraction/todos/{todoId}</c>）：只改期限與負責人，異動紀錄記下來源會議。</summary>
public class ExtractedTodoUpdateRequestDto
{
    public int MeetingId { get; set; }

    public DateTime? DueDate { get; set; }

    public string? Owner { get; set; }
}
