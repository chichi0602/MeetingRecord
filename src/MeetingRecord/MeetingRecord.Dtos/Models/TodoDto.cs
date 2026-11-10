using System.Text.Json.Serialization;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Dtos.Models;

/// <summary>
/// 待辦事項資料傳輸物件（含唯讀的關聯名稱）
/// </summary>
public class TodoDto : TodoCreateUpdateDto
{
    public TodoDto()
    {
    }

    /// <summary>所屬專案名稱（唯讀）</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>來源會議紀錄標題（唯讀）</summary>
    public string? MeetingTitle { get; set; }

    // ---- 1.1.18（WASM 遷移 M3）起多帶的唯讀欄位：只加不改，舊的呼叫端照用 ----

    /// <summary>所屬專案的狀態；「已完成」＝封存，待辦唯讀（管理者除外）。</summary>
    public string? ProjectStatus { get; set; }

    /// <summary>建立者（甲方帳號只能刪自己建立的待辦）。</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>主待辦標題（子項目才有）。</summary>
    public string? ParentTitle { get; set; }

    /// <summary>子項目。只有畫面清單（<c>POST api/v1/Todo/tree</c>）會帶，而且只在有子項目時才有值。</summary>
    public List<TodoDto>? Children { get; set; }

    /// <summary>子項目總數與完成數（不受篩選影響，畫面顯示「子項目 1/3」）。只有畫面清單會帶。</summary>
    public int ChildrenTotal { get; set; }

    public int ChildrenCompleted { get; set; }

    /// <summary>最近 7 天內改過期限的時間；沒改過或超過 7 天為 null。只有畫面清單會帶。</summary>
    public DateTime? DueDateChangedAt { get; set; }

    /// <summary>「期限已變更」的說明文字。只有畫面清單會帶。</summary>
    public string? DueDateChangeText { get; set; }

    /// <summary>
    /// 呼叫者能不能刪這筆：專案負責人或管理者（甲方帳號只能刪自己建立的）、有待辦的修改權限、而且專案沒有封存（管理者除外）。
    /// 只是給畫面決定要不要顯示刪除鈕；真正的檢查在刪除 API。
    /// </summary>
    public bool CanDelete { get; set; }

    // ---- 顯示用（不序列化，1.1.19 WASM 待辦頁）：規則在 Share 的 TodoRules，與 TodoAdapterModel 同一份 ----

    [JsonIgnore]
    public bool IsCompleted => TodoRules.IsCompleted(Status);

    [JsonIgnore]
    public bool IsOverdue => TodoRules.IsOverdue(DueDate, Status);

    [JsonIgnore]
    public int OverdueDays => TodoRules.OverdueDays(DueDate, Status);

    [JsonIgnore]
    public string DueDateOnlyText => TodoRules.DueDateOnlyText(DueDate);

    [JsonIgnore]
    public string DueDateText => TodoRules.DueDateText(DueDate, Status);

    [JsonIgnore]
    public bool HasRecentDueDateChange => DueDateChangedAt is not null;

    [JsonIgnore]
    public bool IsSubtask => ParentTodoId is not null;

    [JsonIgnore]
    public bool IsProjectArchived => ProjectStatus == ProjectScopeRules.ArchivedStatus;

    /// <summary>編輯前複製一份，取消時不影響清單（子項目清單只讀不改，淺複製即可）。</summary>
    public TodoDto Clone() => (TodoDto)MemberwiseClone();
}
