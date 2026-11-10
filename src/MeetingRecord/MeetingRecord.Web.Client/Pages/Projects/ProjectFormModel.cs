using System.ComponentModel.DataAnnotations;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Client.Pages.Projects;

/// <summary>
/// 專案表單的繫結模型（1.1.26 搬到 WASM）。驗證訊息與 Server 的 <c>ProjectAdapterModel</c> 一字不差（伺服器存檔時也會用那一份再驗一次）。
/// </summary>
public sealed class ProjectFormModel : IValidatableObject
{
    public int Id { get; set; }

    [Required(ErrorMessage = "專案標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = ProjectRules.StatusOptions[0];

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    /// <summary>負責人姓名快照（只做顯示：對不到帳號的舊資料顯示「原負責人」）。</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>⚠️ 刻意不掛 [Required]（同 ProjectAdapterModel）：必填檢查在伺服器，只在使用者能換負責人時才要求。</summary>
    public int? OwnerUserId { get; set; }

    [Required(ErrorMessage = "主責團隊 不可為空白")]
    public int? PrimaryTeamId { get; set; }

    public string PrimaryTeamName { get; set; } = string.Empty;

    public List<int> CollaboratorTeamIds { get; set; } = [];

    public List<string> Categories { get; set; } = [];

    public List<string> GlossaryTerms { get; set; } = [];

    public List<string> Participants { get; set; } = [];

    /// <summary>已上傳的附件（「移除」只從這裡拿掉，存檔時才真的刪）。</summary>
    public List<ProjectFileDto> Files { get; set; } = [];

    public bool IsArchived => Status == ProjectScopeRules.ArchivedStatus;

    /// <summary>從單筆 API 的結果複製一份（清單另建新實例，取消時不影響畫面上的專案）。</summary>
    public static ProjectFormModel From(ProjectDto project) => new()
    {
        Id = project.Id ?? 0,
        Title = project.Title,
        StartDate = project.StartDate,
        EndDate = project.EndDate,
        Status = project.Status,
        CompletionPercentage = project.CompletionPercentage,
        Owner = project.Owner ?? string.Empty,
        OwnerUserId = project.OwnerUserId,
        PrimaryTeamId = project.PrimaryTeamId,
        PrimaryTeamName = project.PrimaryTeamName,
        CollaboratorTeamIds = [.. project.CollaboratorTeamIds ?? []],
        Categories = [.. project.Categories ?? []],
        GlossaryTerms = [.. project.GlossaryTerms ?? []],
        Participants = [.. project.Participants ?? []],
        Files = [.. project.Files],
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate.Value < StartDate.Value)
        {
            yield return new ValidationResult("結束日期 不可早於開始日期", [nameof(EndDate)]);
        }
    }
}
