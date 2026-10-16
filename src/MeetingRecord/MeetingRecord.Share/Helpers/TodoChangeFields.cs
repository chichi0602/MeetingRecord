namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 待辦異動紀錄的欄位代碼與顯示名稱（1.0.44）。資料表存代碼，畫面與通知文字用顯示名稱。
/// </summary>
public static class TodoChangeFields
{
    public const string DueDate = "DueDate";
    public const string Owner = "Owner";
    public const string Status = "Status";
    public const string Title = "Title";
    public const string Priority = "Priority";

    /// <summary>主待辦底下新增或移除子項目（1.0.45）。只記在主待辦的異動紀錄，不通知。</summary>
    public const string Subtask = "Subtask";

    /// <summary>
    /// 會發通知的欄位：期限、負責人、狀態。標題、優先度只記異動不通知——
    /// 那兩項常常是順手修字，每改一次就通知全專案會變成噪音。
    /// </summary>
    public static readonly IReadOnlySet<string> Notifying = new HashSet<string>(StringComparer.Ordinal)
    {
        DueDate, Owner, Status,
    };

    public static string Describe(string field) => field switch
    {
        DueDate => "期限",
        Owner => "負責人",
        Status => "狀態",
        Title => "標題",
        Priority => "優先度",
        Subtask => "子項目",
        _ => field,
    };

    /// <summary>異動紀錄的「來源」欄：手動、API、從哪一場會議抽出。</summary>
    public static string DescribeVia(string via, string? sourceMeetingTitle) => via switch
    {
        AuditVia.AiExtract => string.IsNullOrWhiteSpace(sourceMeetingTitle) ? "從會議紀錄抽出" : $"從〈{sourceMeetingTitle}〉抽出",
        AuditVia.Api => "API",
        _ => "手動修改",
    };
}
