using MeetingRecord.Share.Enums;

namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 待辦的選項與顯示規則（1.1.19，WASM 遷移 M3）：從 <c>TodoAdapterModel</c> 抽出來，伺服器（服務層、Server 頁）
/// 與 WASM 頁（<c>TodoDto</c>）共用一份，「逾期」「截止日文字」不會兩邊各算各的。
/// </summary>
public static class TodoRules
{
    /// <summary>狀態選項。順序即畫面下拉的順序，第一項為新增時的預設值。</summary>
    public static readonly IReadOnlyList<string> StatusOptions = ["待辦", "進行中", "已完成"];

    /// <summary>優先度選項。索引 1（中）為新增時的預設值。</summary>
    public static readonly IReadOnlyList<string> PriorityOptions = ["低", "中", "高"];

    /// <summary>代表「已完成」的狀態值。勾選完成與各處判斷都以此為準。</summary>
    public const string CompletedStatus = "已完成";

    public static bool IsCompleted(string? status) => string.Equals(status, CompletedStatus, StringComparison.Ordinal);

    /// <summary>已逾期：有截止日、尚未完成、且截止日早於今天。</summary>
    public static bool IsOverdue(DateTime? dueDate, string? status)
        => dueDate.HasValue && !IsCompleted(status) && dueDate.Value.Date < DateTime.Today;

    /// <summary>逾期天數（未逾期時為 0）。</summary>
    public static int OverdueDays(DateTime? dueDate, string? status)
        => IsOverdue(dueDate, status) ? (DateTime.Today - dueDate!.Value.Date).Days : 0;

    /// <summary>只有截止日本身的顯示文字（不含逾期天數）。清單的截止日欄用它，逾期另外拆成一個標記，避免長字串斷在半路。</summary>
    public static string DueDateOnlyText(DateTime? dueDate) => dueDate.HasValue ? dueDate.Value.ToString("yyyy/MM/dd") : "未指定";

    /// <summary>截止日顯示文字，逾期時附上天數。窄版面（右側面板）用這個單一字串。</summary>
    public static string DueDateText(DateTime? dueDate, string? status)
    {
        if (!dueDate.HasValue)
        {
            return "未指定";
        }

        var text = dueDate.Value.ToString("yyyy/MM/dd");
        // 刻意不用全形括號：那兩個字元很寬，在窄欄位裡會把字串推到換行。
        return IsOverdue(dueDate, status) ? $"{text} 逾期 {OverdueDays(dueDate, status)} 天" : text;
    }
}

/// <summary>專案「已完成＝封存」與「使用中｜已完成｜全部」檢視（1.0.46；1.1.19 抽到 Share，WASM 頁也用）。</summary>
public static class ProjectScopeRules
{
    /// <summary>專案狀態「已完成」＝封存，底下的資料唯讀（管理者除外）。</summary>
    public const string ArchivedStatus = "已完成";

    public static bool Matches(string? status, ProjectScope scope) => scope switch
    {
        ProjectScope.Active => status != ArchivedStatus,
        ProjectScope.Archived => status == ArchivedStatus,
        _ => true,
    };
}
