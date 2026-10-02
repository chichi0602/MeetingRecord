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

    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = StatusOptions[0];

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    /// <summary>負責人，手打的描述欄位（不影響任何權限；誰看得到由主責與協作團隊決定）。</summary>
    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

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
