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

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

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
