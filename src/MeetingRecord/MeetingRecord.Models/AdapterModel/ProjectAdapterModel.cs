using System.ComponentModel.DataAnnotations;

namespace MeetingRecord.Models.AdapterModel;

public class ProjectAdapterModel : ICloneable, IValidatableObject
{
    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "未開始",
        "進行中",
        "已完成",
        "暫緩",
        "等待"
    ];

    /// <summary>
    /// 已完成＝封存（1.0.46）：預設不在專案選單與待辦清單出現，一般使用者唯讀（管理者不受限），
    /// 狀態改回其他任一種就是重新開啟。規則入口在 <c>ProjectArchiveRules</c>。
    /// </summary>
    public const string ArchivedStatus = "已完成";

    public bool IsArchived => Status == ArchivedStatus;

    /// <summary>專案下拉的選項文字：已完成的加註，在「全部」檢視裡一眼分得出來（1.0.46）。</summary>
    public string SelectorLabel => IsArchived ? $"{Title}（已完成）" : Title;

    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = StatusOptions[0];

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    /// <summary>
    /// 負責人姓名快照（1.0.52 起由服務依 <see cref="OwnerUserId"/> 填入，畫面不直接編輯）。
    /// 升級前手打、對不到帳號的舊值仍留在這裡，畫面顯示成「未指定（原：XXX）」。
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// 負責人帳號（1.0.52）：從主責＋協作團隊裡啟用中的帳號選。
    /// 負責人（與管理者）可以刪除這個專案，以及底下任何人的會議、待辦、附件。
    /// ⚠️ 刻意不掛 [Required]：沒有權限換負責人的人（協作團隊成員）編輯舊專案時，負責人照原樣保留、可能是 null；
    /// 必填檢查在 <c>ProjectService</c>，只在使用者能換負責人時才要求。
    /// </summary>
    public int? OwnerUserId { get; set; }

    /// <summary>還沒有對應到帳號的負責人（升級前手打、對不到人的舊資料）。</summary>
    public bool IsOwnerUnassigned => OwnerUserId is null;

    /// <summary>
    /// 主責團隊（0.4.102，必填）。團隊＝「誰的資料」：使用者所屬團隊與主責＋協作有交集才看得到。
    /// 存檔時由 <c>ProjectService</c> 同步成 <c>ProjectTeam</c>。
    /// </summary>
    [Required(ErrorMessage = "主責團隊 不可為空白")]
    public int? PrimaryTeamId { get; set; }

    /// <summary>協作團隊，0～多個。</summary>
    public List<int> CollaboratorTeamIds { get; set; } = [];

    /// <summary>主責團隊名稱，只做顯示。</summary>
    public string PrimaryTeamName { get; set; } = string.Empty;

    /// <summary>協作團隊名稱，只做顯示。</summary>
    public List<string> CollaboratorTeamNames { get; set; } = [];

    /// <summary>分類（0.4.102）。分類＝「什麼資料」，只描述性質，不影響誰看得到。</summary>
    public List<string> Categories { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>常用名詞（術語、產品名），餵給 AI 校正語音辨識聽錯的同音字。</summary>
    public List<string> GlossaryTerms { get; set; } = [];

    /// <summary>常用與會人員名冊。產生會議紀錄時從這裡勾選本次實際到場的人。</summary>
    public List<string> Participants { get; set; } = [];

    public List<ProjectFileAdapterModel> Files { get; set; } = [];

    public ProjectAdapterModel Clone()
    {
        var cloned = ((ICloneable)this).Clone() as ProjectAdapterModel ?? new ProjectAdapterModel();

        // MemberwiseClone 是淺複製，清單必須另建新實例——否則在編輯對話框裡加一個名詞，
        // 即使按「取消」也已經改到清單資料列上了。比照 PromptTemplateAdapterModel.Clone()。
        cloned.GlossaryTerms = [.. GlossaryTerms];
        cloned.Participants = [.. Participants];
        cloned.Files = [.. Files];
        cloned.CollaboratorTeamIds = [.. CollaboratorTeamIds];
        cloned.CollaboratorTeamNames = [.. CollaboratorTeamNames];
        cloned.Categories = [.. Categories];

        return cloned;
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate.Value < StartDate.Value)
        {
            yield return new ValidationResult("結束日期 不可早於開始日期", [nameof(EndDate)]);
        }
    }
}
