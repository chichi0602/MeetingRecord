namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 待辦的異動紀錄（1.0.44）：誰、何時、把哪個欄位從什麼改成什麼。一個欄位一列。
///
/// <para>
/// 使用者問「有人改了期限，一起負責的人怎麼知道」——稽核紀錄只有管理員看得到，這張表是給專案成員看的。
/// 寫入一律經 <c>TodoChangeRecorder</c>，與待辦本身同一次存檔。
/// 待辦刪除時一併刪除（明確 Cascade，全站預設 Restrict）。
/// </para>
/// </summary>
public class TodoChange
{
    public int Id { get; set; }

    public int TodoId { get; set; }

    public Todo? Todo { get; set; }

    /// <summary>異動當下的所屬專案（待辦移專案時記新的那個）。</summary>
    public int ProjectId { get; set; }

    /// <summary>欄位代碼：DueDate、Owner、Status、Title、Priority（見 <c>TodoChangeFields</c>）。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>舊值（顯示用字串；期限為 yyyy/MM/dd）。</summary>
    public string? OldValue { get; set; }

    /// <summary>新值（顯示用字串；期限為 yyyy/MM/dd）。</summary>
    public string? NewValue { get; set; }

    public int? ChangedByUserId { get; set; }

    /// <summary>操作者顯示名稱（快照，帳號改名或刪除後仍看得出是誰）。</summary>
    public string? ChangedByName { get; set; }

    /// <summary>異動時間（本地時間，與待辦的 CreatedAt／UpdatedAt 一致）。</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>從哪裡改的：Web、Api、AiExtract（見 <c>AuditVia</c>）。</summary>
    public string Via { get; set; } = string.Empty;

    /// <summary>從會議紀錄抽出待辦時更新的，記下是哪一場會議（無 FK：會議刪了紀錄照樣留著）。</summary>
    public int? SourceMeetingId { get; set; }

    /// <summary>來源會議標題（快照）。</summary>
    public string? SourceMeetingTitle { get; set; }
}
